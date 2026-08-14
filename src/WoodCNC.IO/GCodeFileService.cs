using System.Text;

namespace WoodCNC.IO;

public sealed class GCodeFileService
{
    public ImportedGCodeFile Import(string path, bool? forceXor = null)
    {
        var bytes = File.ReadAllBytes(path);

        if (forceXor == true)
        {
            return new ImportedGCodeFile(path, DecodeText(JueCodeCodec.Transform(bytes)), true, "按 XOR 文件读取");
        }

        if (forceXor == false)
        {
            return new ImportedGCodeFile(path, DecodeText(bytes), false, "按明文 G-code 读取");
        }

        var plainText = DecodeText(bytes);
        var decoded = DecodeText(JueCodeCodec.Transform(bytes));
        var plainScore = GetGCodeScore(plainText);
        var decodedScore = GetGCodeScore(decoded);

        if (plainScore > 0 && plainScore >= decodedScore)
        {
            return new ImportedGCodeFile(path, plainText, false, "自动识别为明文 G-code");
        }

        if (decodedScore > 0)
        {
            return new ImportedGCodeFile(path, decoded, true, "自动识别为 XOR 旧文件");
        }

        return new ImportedGCodeFile(path, plainText, false, "未识别到典型 G-code，按明文读取并保留原文");
    }

    public void ExportPlain(string path, string text)
    {
        File.WriteAllText(path, text, Encoding.UTF8);
    }

    public void ExportXor(string path, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        File.WriteAllBytes(path, JueCodeCodec.Transform(bytes));
    }

    private static string DecodeText(byte[] bytes)
    {
        return Encoding.UTF8.GetString(bytes);
    }

    private static int GetGCodeScore(string text)
    {
        var score = 0;
        foreach (var rawLine in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = rawLine.Trim().ToUpperInvariant();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("G0")
                || line.StartsWith("G1")
                || line.StartsWith("G2")
                || line.StartsWith("G3")
                || line.StartsWith("G4")
                || line.StartsWith("M"))
            {
                score++;
            }
        }

        return score;
    }
}

