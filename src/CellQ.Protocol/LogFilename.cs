using System.Text;

namespace CellQ.Protocol;

public static class LogFilename
{
    public static string Suggested(DeviceLog log)
    {
        string previous = log.Folder + "_" + log.Filename;
        if (string.IsNullOrWhiteSpace(log.PresetName)) return previous;
        var name = new StringBuilder();
        foreach (char value in log.PresetName)
            name.Append(char.IsControl(value) || "<>:\"/\\|?*".Contains(value) ? '_' : value);
        string prefix = name.ToString().Trim(' ', '.', '_');
        if (prefix.Length > 80)
        {
            prefix = prefix[..80];
            if (char.IsHighSurrogate(prefix[^1])) prefix = prefix[..^1];
            prefix = prefix.TrimEnd(' ', '.', '_');
        }
        return prefix.Length == 0 ? previous : prefix + "_" + previous;
    }
}
