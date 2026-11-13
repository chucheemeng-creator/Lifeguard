namespace Lifeguard;

/// <summary>
/// 讀取 .env 檔(KEY=VALUE),交給 Configuration 使用。
/// 規則:
///   - 空白行與 # 開頭的行忽略
///   - 以第一個 = 分隔,key 與 value 前後空白去除
///   - value 若被 "..." 或 '...' 包住,去掉外層引號
///   - 檔案不存在 → 回傳空集合(不是錯誤)
/// 不支援變數展開、跨行值;Lifeguard 只需要放幾個簡單的密鑰。
/// </summary>
public static class DotEnv
{
    public static Dictionary<string, string?> Load(string path)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(path))
            return values;

        foreach (string rawLine in File.ReadLines(path))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;

            int eq = line.IndexOf('=');
            if (eq <= 0)
                continue;

            string key = line[..eq].Trim();
            string value = line[(eq + 1)..].Trim();

            if (value.Length >= 2 &&
                ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }

            if (key.Length > 0)
                values[key] = value;
        }

        return values;
    }
}
