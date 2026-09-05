namespace InspectAzureAI.Eval.Context;

/// <summary>
/// Port of the <c>_Tree</c> of <c>util/_limit.py</c>: the leaf scope of one kind of limit in the current async
/// flow plus a suspension count, both AsyncLocal so nested flows (tool calls run as concurrent tasks) see
/// their ancestors' scopes and never a sibling's. Each entered limit becomes a child of the current leaf,
/// which is what makes this a tree rather than a stack.
/// </summary>
public sealed class LimitTree<TNode> where TNode : Limit
{
    private readonly AsyncLocal<TNode?> _leaf = new();

    private readonly AsyncLocal<int> _suspended = new();

    /// <summary>The innermost limit of this kind in the current async flow, or null.</summary>
    public TNode? Leaf => _leaf.Value;

    /// <summary>The outermost limit of this kind in the current async flow, or null when the tree is empty.</summary>
    public TNode? Root
    {
        get
        {
            var node = _leaf.Value;
            while (node?.Parent is TNode parent)
            {
                node = parent;
            }

            return node;
        }
    }

    /// <summary>True inside a <see cref="Suspended"/> block: recording and checks of this kind are no-ops.</summary>
    public bool IsSuspended => _suspended.Value > 0;

    public void Push(TNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        node.Parent = _leaf.Value;
        _leaf.Value = node;
    }

    /// <summary>Pops <paramref name="node"/>, which must be the leaf: scopes are closed in stack order like Python's <c>with</c>.</summary>
    public void Pop(TNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var leaf = _leaf.Value ?? throw new InvalidOperationException("Limit tree is empty. Cannot pop from an empty tree.");
        if (!ReferenceEquals(leaf, node))
        {
            throw new InvalidOperationException(
                "The limit scope being closed is not the leaf node in the tree. Make sure to open and close the "
                + "scopes in a stack-like manner using a 'using' statement.");
        }

        _leaf.Value = leaf.Parent as TNode;
    }

    /// <summary>Port of <c>suspended()</c>: suspends this kind of limit (nested scopes included) until the result is disposed.</summary>
    public IDisposable Suspended()
    {
        _suspended.Value++;
        return new Resume(this);
    }

    private sealed class Resume(LimitTree<TNode> tree) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            tree._suspended.Value--;
        }
    }
}
