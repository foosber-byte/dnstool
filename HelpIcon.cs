using System.Drawing;
using System.Windows.Forms;

namespace DnsToolWinForms
{
    /// <summary>
    /// Маленький значок "?" с всплывающей подсказкой (ToolTip) при наведении - замена
    /// постоянно видимому серому тексту-подсказке под полями. Экономит место в интерфейсе:
    /// подсказка видна только когда реально нужна, а не всегда занимает строку.
    /// </summary>
    public static class HelpIcon
    {
        public static Label Create(ToolTip toolTip, string text)
        {
            const int size = 15; // маленький, компактный - не должен спорить с остальным интерфейсом

            var lbl = new Label
            {
                Text = "?",
                AutoSize = false,
                Size = new Size(size, size),
                Font = new Font("Segoe UI", 7.5F, FontStyle.Bold),
                ForeColor = Color.FromArgb(70, 100, 130),   // приглушённый сине-серый - контраст на пастели
                BackColor = Color.FromArgb(214, 234, 248),  // светло-синяя пастель, не резкий SteelBlue
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Help,
                Margin = new Padding(4, 4, 0, 0)
            };

            // Обычный Label прямоугольный - делаем его круглым через Region (стандартный приём
            // WinForms для скруглённых элементов без сторонних библиотек).
            using (var path = new System.Drawing.Drawing2D.GraphicsPath())
            {
                path.AddEllipse(0, 0, size - 1, size - 1);
                lbl.Region = new Region(path);
            }

            toolTip.SetToolTip(lbl, text);
            // Подсказка должна появляться быстро и держаться подольше - по умолчанию у ToolTip
            // задержка показа неудобно большая для такого маленького элемента.
            toolTip.InitialDelay = 300;
            toolTip.AutoPopDelay = 15000;
            toolTip.ReshowDelay = 100;
            return lbl;
        }
    }
}
