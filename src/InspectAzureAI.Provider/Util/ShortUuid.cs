using System.Security.Cryptography;

namespace InspectAzureAI.Provider.Util;

/// <summary>
/// Port of <c>shortuuid.uuid()</c> as used by <c>src/inspect_ai/_util/_uuid.py</c>: a 22-character id
/// drawn from the shortuuid alphabet.
/// </summary>
public static class ShortUuid
{
    /// <summary>The shortuuid default alphabet (no ambiguous characters).</summary>
    public const string Alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

    /// <summary>Length of generated ids.</summary>
    public const int Length = 22;

    public static string Generate()
    {
        var chars = new char[Length];
        for (var i = 0; i < Length; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return new string(chars);
    }
}
