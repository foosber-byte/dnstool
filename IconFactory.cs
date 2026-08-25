using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DnsToolWinForms
{
    /// <summary>
    /// Простые монохромные иконки для кнопок тулбара - рисуются сами через GDI+, а не берутся
    /// из шрифта или внешнего файла. Всегда доступны, чёткие на любом DPI, в стиле остального
    /// приложения (та же техника, что у круглого значка "?" в HelpIcon.cs).
    /// </summary>
    public static class IconFactory
    {
        private const int Size = 18;
        private static readonly Color PenColor = Color.FromArgb(90, 90, 90);

        private static Bitmap NewCanvas(out Graphics g)
        {
            var bmp = new Bitmap(Size, Size);
            g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            return bmp;
        }

        public static Bitmap Refresh()
        {
            var bmp = NewCanvas(out var g);
            using var pen = new Pen(PenColor, 1.6f);
            g.DrawArc(pen, 3, 3, 12, 12, -30, 270);
            // стрелка на конце дуги
            var arrow = new[] { new Point(14, 3), new Point(16, 6), new Point(12, 6) };
            using var brush = new SolidBrush(PenColor);
            g.FillPolygon(brush, arrow);
            g.Dispose();
            return bmp;
        }

        public static Bitmap Add()
        {
            var bmp = NewCanvas(out var g);
            using var pen = new Pen(PenColor, 2f);
            g.DrawLine(pen, 9, 3, 9, 15);
            g.DrawLine(pen, 3, 9, 15, 9);
            g.Dispose();
            return bmp;
        }

        public static Bitmap Edit()
        {
            var bmp = NewCanvas(out var g);
            using var pen = new Pen(PenColor, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(pen, 4, 14, 12, 4); // корпус карандаша
            using var tipBrush = new SolidBrush(PenColor);
            g.FillPolygon(tipBrush, new[] { new Point(3, 15), new Point(4, 14), new Point(5, 15) }); // грифель
            g.DrawLine(pen, 10, 2, 15, 6); // кончик (насадка)
            g.Dispose();
            return bmp;
        }

        public static Bitmap Delete()
        {
            var bmp = NewCanvas(out var g);
            using var pen = new Pen(PenColor, 1.5f);
            g.DrawRectangle(pen, 4, 5, 10, 10); // корзина
            g.DrawLine(pen, 2, 5, 16, 5);       // крышка
            g.DrawLine(pen, 7, 2, 11, 2);       // ручка
            g.DrawLine(pen, 7, 2, 7, 5);
            g.DrawLine(pen, 11, 2, 11, 5);
            g.DrawLine(pen, 7, 8, 7, 12);
            g.DrawLine(pen, 11, 8, 11, 12);
            g.Dispose();
            return bmp;
        }

        public static Bitmap Search()
        {
            var bmp = NewCanvas(out var g);
            using var pen = new Pen(PenColor, 1.6f);
            g.DrawEllipse(pen, 3, 3, 9, 9);
            g.DrawLine(pen, 12, 12, 16, 16);
            g.Dispose();
            return bmp;
        }

        public static Bitmap Export()
        {
            var bmp = NewCanvas(out var g);
            using var pen = new Pen(PenColor, 1.6f);
            g.DrawLine(pen, 9, 2, 9, 11);
            var arrow = new[] { new Point(5, 8), new Point(9, 12), new Point(13, 8) };
            g.DrawLines(pen, arrow);
            g.DrawLine(pen, 3, 15, 15, 15); // лоток
            g.Dispose();
            return bmp;
        }

        public static Bitmap Import()
        {
            var bmp = NewCanvas(out var g);
            using var pen = new Pen(PenColor, 1.6f);
            g.DrawLine(pen, 9, 7, 9, 16);
            var arrow = new[] { new Point(5, 10), new Point(9, 6), new Point(13, 10) }; // стрелка вверх - зеркально Export
            g.DrawLines(pen, arrow);
            g.DrawLine(pen, 3, 3, 15, 3); // "источник" сверху, тоже зеркально
            g.Dispose();
            return bmp;
        }

        public static Bitmap Check()
        {
            var bmp = NewCanvas(out var g);
            using var pen = new Pen(Color.FromArgb(30, 130, 76), 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(pen, new[] { new Point(3, 9), new Point(7, 13), new Point(15, 4) });
            g.Dispose();
            return bmp;
        }

        public static Bitmap Notepad()
        {
            var bmp = NewCanvas(out var g);
            using var pen = new Pen(PenColor, 1.4f);
            g.DrawRectangle(pen, 3, 4, 12, 12); // страница
            // спиральный переплёт сверху - несколько маленьких колечек
            using var ringPen = new Pen(PenColor, 1.1f);
            for (int x = 5; x <= 13; x += 4)
                g.DrawEllipse(ringPen, x, 2, 2, 3);
            // строчки текста внутри
            g.DrawLine(pen, 5, 9, 13, 9);
            g.DrawLine(pen, 5, 12, 13, 12);
            g.DrawLine(pen, 5, 15, 10, 15);
            g.Dispose();
            return bmp;
        }

        public static Bitmap Folder()
        {
            var bmp = NewCanvas(out var g);
            using var pen = new Pen(PenColor, 1.4f);
            g.DrawLine(pen, 2, 5, 7, 5);
            g.DrawLine(pen, 7, 5, 9, 7);
            g.DrawRectangle(pen, 2, 7, 14, 8);
            g.Dispose();
            return bmp;
        }

        /// <summary>
        /// Создаёт кнопку-иконку фиксированного размера с подсказкой при наведении - единый
        /// вид для всех тулбаров приложения. Клик передаётся через обычный EventHandler.
        /// </summary>
        public static Button CreateButton(Bitmap icon, string tooltip, ToolTip toolTip, System.EventHandler onClick)
        {
            var btn = new Button
            {
                Image = icon,
                Size = new Size(30, 28),
                Margin = new Padding(2, 2, 2, 2),
                FlatStyle = FlatStyle.Standard
            };
            toolTip.SetToolTip(btn, tooltip);
            btn.Click += onClick;
            return btn;
        }
    }
}
