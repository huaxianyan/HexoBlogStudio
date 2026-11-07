using System.IO;
using System.Text.RegularExpressions;

namespace HexoBlogStudio.Services;

/// <summary>
/// Hexo front-matter 里本站会用到的字段。只认「键: 值」「键: [a, b]」「键: 加若干条 - 项」
/// 这几种写法，不引入 YAML 依赖：文章头部是人工维护的简单结构，多余的写法按纯文本处理。
/// </summary>
public sealed class FrontMatter
{
    public string? Title { get; set; }
    public string? Date { get; set; }
    public List<string> Tags { get; } = [];
    public List<string> Categories { get; } = [];

    private static readonly Regex InlineList = new(@"^\[(.*)\]$", RegexOptions.Compiled);

    public static FrontMatter Parse(string text)
    {
        var fm = new FrontMatter();
        var lines = text.Split('\n');
        if (lines.Length == 0 || lines[0].TrimEnd('\r').Trim() != "---") return fm;

        string? pendingListKey = null;
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Trim() == "---") break;
            if (line.Trim().Length == 0) continue;

            // 列表项：跟在上一个只有键没有值的字段后面
            if (line.TrimStart().StartsWith("- ") && pendingListKey is not null)
            {
                AddListItem(fm, pendingListKey, Unquote(line.TrimStart()[2..].Trim()));
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();

            if (value.Length == 0)
            {
                pendingListKey = key;
                continue;
            }

            pendingListKey = null;
            var inline = InlineList.Match(value);
            if (inline.Success)
            {
                foreach (var part in inline.Groups[1].Value.Split(','))
                {
                    var item = Unquote(part.Trim());
                    if (item.Length > 0) AddListItem(fm, key, item);
                }
                continue;
            }

            switch (key)
            {
                case "title": fm.Title = Unquote(value); break;
                case "date": fm.Date = Unquote(value); break;
            }
        }
        return fm;
    }

    private static void AddListItem(FrontMatter fm, string key, string value)
    {
        if (value.Length == 0) return;
        if (key == "tags") fm.Tags.Add(value);
        else if (key == "categories") fm.Categories.Add(value);
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            return value[1..^1];
        return value;
    }
}
