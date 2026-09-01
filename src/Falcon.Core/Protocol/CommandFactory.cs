using System.Text;

namespace Falcon.Core.Protocol;

/// <summary>Builds command strings (the transport appends the CR).</summary>
public static class CommandFactory
{
    public static string Build(params string?[] parts)
    {
        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part)) continue;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(part);
        }
        return sb.ToString();
    }
}
