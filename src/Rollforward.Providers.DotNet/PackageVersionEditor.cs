using System.Text;
using System.Text.RegularExpressions;

namespace Rollforward.Providers.DotNet;

/// <summary>
/// Rewrites the version of specific PackageReference/PackageVersion entries with a surgical text
/// edit, the same way the target-framework bump does: only the version text changes, so line
/// endings, indentation, comments, attribute order and a leading BOM all survive and the diff in
/// the PR is exactly the versions.
///
/// Entries are addressed by <see cref="StalePackage.ElementIndex"/> (document order, comments not
/// counted) rather than found again by name, so the entry edited is exactly the one that was
/// detected — a package declared twice (a project and a props file) or conditionally is never
/// confused. Anything that doesn't look as expected on the way (the id doesn't match, no version
/// where one was detected) is left untouched and reported back, not guessed at.
/// </summary>
internal static partial class PackageVersionEditor
{
    public sealed record Result(IReadOnlySet<string> ChangedFiles, IReadOnlyList<StalePackage> NotEdited);

    /// <summary>Sets each package's version to <paramref name="newVersionById"/>[its id]; packages with no entry there are not edited.</summary>
    public static Result Apply(IEnumerable<StalePackage> packages, IReadOnlyDictionary<string, string> newVersionById)
    {
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var notEdited = new List<StalePackage>();

        foreach (var file in packages.GroupBy(p => p.File, StringComparer.OrdinalIgnoreCase))
        {
            var hasBom = File.ReadAllBytes(file.Key) is [0xEF, 0xBB, 0xBF, ..];
            var original = File.ReadAllText(file.Key);
            var text = original;

            foreach (var package in file)
            {
                if (!newVersionById.TryGetValue(package.Id, out var newVersion)
                    || TryRewrite(text, package, newVersion) is not { } rewritten)
                {
                    notEdited.Add(package);
                    continue;
                }

                text = rewritten; // only version text changed, so element positions are still valid
            }

            if (text != original)
            {
                File.WriteAllText(file.Key, text, new UTF8Encoding(hasBom));
                changed.Add(file.Key);
            }
        }

        return new Result(changed, notEdited);
    }

    /// <summary>The text with the package's version replaced, or null if the entry isn't what was detected.</summary>
    private static string? TryRewrite(string text, StalePackage package, string newVersion)
    {
        var start = FindElementStart(text, package.ElementIndex);
        if (start < 0)
            return null;

        var tagEnd = FindTagEnd(text, start);
        if (tagEnd < 0)
            return null;

        var tag = text.Substring(start, tagEnd - start + 1);
        var attributes = AttributePattern().Matches(tag);

        var id = attributes.FirstOrDefault(a => a.Groups["name"].Value is "Include" or "Update");
        if (id is null || !string.Equals(id.Groups["val"].Value.Trim(), package.Id, StringComparison.OrdinalIgnoreCase))
            return null;

        // Same precedence the detection used: Version, then VersionOverride, then a child element.
        var versionAttribute = attributes.FirstOrDefault(a => a.Groups["name"].Value == "Version")
            ?? attributes.FirstOrDefault(a => a.Groups["name"].Value == "VersionOverride");
        if (versionAttribute is not null)
        {
            var value = versionAttribute.Groups["val"];
            if (value.Value.Trim() != package.Version)
                return null;
            return Splice(text, start + value.Index, value.Length, newVersion);
        }

        if (tag.EndsWith("/>", StringComparison.Ordinal))
            return null;

        var close = ClosingTagPattern().Match(text, tagEnd + 1);
        if (!close.Success)
            return null;

        var body = text.Substring(tagEnd + 1, close.Index - tagEnd - 1);
        var child = VersionElementPattern().Match(body);
        if (!child.Success || child.Groups["val"].Value.Trim() != package.Version)
            return null;

        var childValue = child.Groups["val"];
        var leading = childValue.Value.Length - childValue.Value.TrimStart().Length;
        return Splice(text, tagEnd + 1 + childValue.Index + leading, childValue.Value.Trim().Length, newVersion);
    }

    private static string Splice(string text, int index, int length, string replacement) =>
        string.Concat(text.AsSpan(0, index), replacement, text.AsSpan(index + length));

    /// <summary>Index of the "&lt;" of the Nth (0-based) PackageReference/PackageVersion start tag, skipping comments; -1 if there are fewer.</summary>
    private static int FindElementStart(string text, int elementIndex)
    {
        var seen = -1;
        foreach (Match m in ElementStartOrCommentPattern().Matches(text))
        {
            if (!m.Groups["element"].Success)
                continue; // a comment
            if (++seen == elementIndex)
                return m.Index;
        }

        return -1;
    }

    /// <summary>Index of the "&gt;" that ends the start tag beginning at <paramref name="start"/>, ignoring any inside quoted attribute values.</summary>
    private static int FindTagEnd(string text, int start)
    {
        char? quote = null;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (quote is not null)
            {
                if (c == quote) quote = null;
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '>')
            {
                return i;
            }
        }

        return -1;
    }

    [GeneratedRegex(@"<!--.*?-->|<(?<element>PackageReference|PackageVersion)(?=[\s/>])", RegexOptions.Singleline)]
    private static partial Regex ElementStartOrCommentPattern();

    [GeneratedRegex(@"(?<name>[\w:.\-]+)\s*=\s*(?<q>[""'])(?<val>.*?)\k<q>", RegexOptions.Singleline)]
    private static partial Regex AttributePattern();

    [GeneratedRegex(@"</(PackageReference|PackageVersion)\s*>")]
    private static partial Regex ClosingTagPattern();

    [GeneratedRegex(@"<(?<n>Version|VersionOverride)>(?<val>[^<]*)</\k<n>>")]
    private static partial Regex VersionElementPattern();
}
