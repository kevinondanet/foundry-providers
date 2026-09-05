using System.Collections;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;

namespace InspectAzureAI.Eval.Dataset;

/// <summary>Port of <c>dataset/_dataset.py</c> <c>MemoryDataset</c>: samples held in an in-memory list.</summary>
public sealed class MemoryDataset : IDataset
{
    private List<Sample> _samples;

    public MemoryDataset(IEnumerable<Sample> samples, string? name = null, string? location = null, bool shuffled = false)
    {
        ArgumentNullException.ThrowIfNull(samples);
        _samples = samples.ToList();
        Name = name;
        Location = location;
        Shuffled = shuffled;
    }

    public string? Name { get; }

    public string? Location { get; }

    public bool Shuffled { get; private set; }

    public int Count => _samples.Count;

    public Sample this[int index] => _samples[index];

    public IDataset Filter(Func<Sample, bool> predicate, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return new MemoryDataset(_samples.Where(predicate), name ?? Name, Location, Shuffled);
    }

    public void Shuffle(int? seed = null)
    {
        // Random.Shared is not seedable, hence the branch; a seeded Random is deterministic in .NET
        // (like random.Random(seed)) but the sequence differs from Python's, so orders are only stable
        // within .NET.
        var random = seed is null ? Random.Shared : new Random(seed.Value);
        for (var i = _samples.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (_samples[i], _samples[j]) = (_samples[j], _samples[i]);
        }

        Shuffled = true;
    }

    public void ShuffleChoices(int? seed = null)
    {
        var random = seed is null ? Random.Shared : new Random(seed.Value);
        for (var index = 0; index < _samples.Count; index++)
        {
            var sample = _samples[index];
            if (sample.Choices is not { Count: > 0 } choices)
            {
                continue;
            }

            var positions = Enumerable.Range(0, choices.Count).ToList();
            AnswerLabels.ShuffleInPlace(positions, random);
            var shuffled = positions.Select(p => choices[p]).ToList();

            // original position -> the letter it now answers to
            var positionMap = positions.Select((original, current) => (original, letter: AnswerLabels.Character(current))).ToDictionary(pair => pair.original, pair => pair.letter);
            var target = new Target(sample.Target.Values.Select(value => RemapTarget(value, positionMap, choices.Count)).ToList());
            _samples[index] = sample with { Choices = shuffled, Target = target };
        }
    }

    /// <summary>Port of <c>_remap_target</c> for one target value: Python's <c>KeyError</c> on an unknown position is an <see cref="ArgumentException"/>.</summary>
    private static string RemapTarget(string value, IReadOnlyDictionary<int, string> positionMap, int choiceCount) =>
        positionMap.TryGetValue(AnswerLabels.Index(value), out var letter)
            ? letter
            : throw new ArgumentException($"Sample target '{value}' does not reference one of the sample's {choiceCount} choices.");

    public void Sort(bool reverse = false, Func<Sample, IComparable>? key = null)
    {
        key ??= sample => sample.InputLength;
        // OrderBy is stable, matching list.sort; List.Sort is not.
        _samples = (reverse ? _samples.OrderByDescending(key) : _samples.OrderBy(key)).ToList();
    }

    public IDataset Slice(Range range)
    {
        // Python slicing clamps out-of-range bounds (dataset[0:limit] with limit > len is the whole
        // dataset) whereas Range.GetOffsetAndLength throws.
        var start = Math.Clamp(range.Start.GetOffset(_samples.Count), 0, _samples.Count);
        var end = Math.Clamp(range.End.GetOffset(_samples.Count), 0, _samples.Count);
        var length = Math.Max(0, end - start);
        return new MemoryDataset(_samples.GetRange(start, length), Name, Location, Shuffled);
    }

    public IEnumerator<Sample> GetEnumerator() => _samples.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
