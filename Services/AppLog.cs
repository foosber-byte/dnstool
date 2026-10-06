using System;
using System.Drawing;
using System.Windows.Forms;

namespace DnsToolWinForms.Services
{
    public sealed class AppLog
    {
        private const string AdminPromptMarker = "нужны права администратора для локальной работы";

        private RichTextBox _output;
        private readonly Action _adminPromptRequested;
        private bool _adminPromptShown;

        // RichTextBox ещё не существует в момент построения вкладок (InitializeComponent),
        // поэтому лог создаётся до него, а вывод подключается позже через SetOutput.
        public AppLog(Action adminPromptRequested = null)
        {
            _adminPromptRequested = adminPromptRequested;
        }

        public void SetOutput(RichTextBox output)
        {
            _output = output;
        }

        public void AppendLog(string text)
        {
            if (string.IsNullOrEmpty(text) || _output == null) return;

            var lines = text.Replace("\r\n", "\n").Split('\n');
            foreach (var rawLine in lines)
            {
                if (rawLine.Length == 0) continue;
                AppendColoredLine($"[{DateTime.Now:HH:mm:ss}] {rawLine}", ColorForLine(rawLine));

                if (!_adminPromptShown && rawLine.Contains(AdminPromptMarker))
                {
                    _adminPromptShown = true;
                    _adminPromptRequested?.Invoke();
                }
            }
        }

        public void AppendLogStyled(params (string Text, bool Bold, bool Underline)[] parts)
        {
            if (_output == null) return;

            _output.SelectionStart = _output.TextLength;
            _output.SelectionLength = 0;
            _output.SelectionColor = Color.DimGray;
            _output.SelectionFont = new Font(_output.Font, FontStyle.Regular);
            _output.AppendText($"[{DateTime.Now:HH:mm:ss}] ");

            foreach (var (text, bold, underline) in parts)
            {
                var style = FontStyle.Regular;
                if (bold) style |= FontStyle.Bold;
                if (underline) style |= FontStyle.Underline;
                _output.SelectionFont = new Font(_output.Font, style);
                _output.SelectionColor = Color.DimGray;
                _output.AppendText(text);
            }

            _output.SelectionFont = new Font(_output.Font, FontStyle.Regular);
            _output.SelectionColor = _output.ForeColor;
            _output.AppendText(Environment.NewLine);
            _output.ScrollToCaret();
        }

        public void AppendColoredLine(string line, Color color)
        {
            if (_output == null) return;

            _output.SelectionStart = _output.TextLength;
            _output.SelectionLength = 0;
            _output.SelectionColor = color;
            _output.AppendText(line + Environment.NewLine);
            _output.SelectionColor = _output.ForeColor;
            _output.ScrollToCaret();
        }

        private static Color ColorForLine(string line)
        {
            if (line.StartsWith("ОШИБКА:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("ИСКЛЮЧЕНИЕ", StringComparison.OrdinalIgnoreCase))
                return Color.Firebrick;

            if (line.StartsWith("OK:", StringComparison.OrdinalIgnoreCase))
                return Color.SeaGreen;

            return Color.DimGray;
        }
    }
}
