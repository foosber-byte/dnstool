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
            using var pen = new Pen(PenColor, 1.8f);
            g.DrawArc(pen, 3f, 3f, 12f, 12f, -90f, -320f);
            using var brush = new SolidBrush(PenColor);
            g.FillPolygon(brush, new[] { new PointF(11.3f, 1.6f), new PointF(16.3f, 4.6f), new PointF(12.1f, 7.1f) });
            g.Dispose();
            return bmp;
        }

        /// <summary>Тот же круговой значок обновления, но с буквой "R" внутри - для перезагрузки зоны (dnscmd /ZoneReload), чтобы отличать от обычного обновления списка.</summary>
        public static Bitmap RefreshZone()
        {
            var bmp = NewCanvas(out var g);
            using var pen = new Pen(PenColor, 1.7f);
            g.DrawArc(pen, 2.6f, 2.6f, 12.8f, 12.8f, -90f, -320f);
            using var brush = new SolidBrush(PenColor);
            g.FillPolygon(brush, new[] { new PointF(11.6f, 0.9f), new PointF(16.9f, 4.1f), new PointF(12.4f, 6.9f) });
            using var font = new Font("Segoe UI", 6.5f, FontStyle.Bold);
            var textSize = g.MeasureString("R", font);
            g.DrawString("R", font, brush, (Size - textSize.Width) / 2f, (Size - textSize.Height) / 2f + 1);
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
            g.DrawLine(pen, 2.5f, 4.5f, 15.5f, 4.5f);       // крышка
            g.DrawLine(pen, 7f, 4.5f, 7f, 2.7f);            // ручка
            g.DrawLine(pen, 7f, 2.7f, 11f, 2.7f);
            g.DrawLine(pen, 11f, 2.7f, 11f, 4.5f);
            g.DrawLine(pen, 4.6f, 6.6f, 5.4f, 15.3f);       // корпус-трапеция
            g.DrawLine(pen, 5.4f, 15.3f, 12.6f, 15.3f);
            g.DrawLine(pen, 12.6f, 15.3f, 13.4f, 6.6f);
            using var ribPen = new Pen(PenColor, 1.3f);
            g.DrawLine(ribPen, 7.4f, 8.6f, 7.4f, 13.3f);    // рёбра
            g.DrawLine(ribPen, 10.6f, 8.6f, 10.6f, 13.3f);
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
            g.DrawLine(pen, 3f, 11.5f, 3f, 14.5f);          // открытый лоток снизу
            g.DrawLine(pen, 3f, 14.5f, 15f, 14.5f);
            g.DrawLine(pen, 15f, 14.5f, 15f, 11.5f);
            g.DrawLine(pen, 9f, 12f, 9f, 3f);               // стрелка вверх из лотка
            g.DrawLines(pen, new[] { new PointF(5.5f, 6.5f), new PointF(9f, 3f), new PointF(12.5f, 6.5f) });
            g.Dispose();
            return bmp;
        }

        public static Bitmap Import()
        {
            var bmp = NewCanvas(out var g);
            using var pen = new Pen(PenColor, 1.6f);
            g.DrawLine(pen, 3f, 11.5f, 3f, 14.5f);          // открытый лоток снизу
            g.DrawLine(pen, 3f, 14.5f, 15f, 14.5f);
            g.DrawLine(pen, 15f, 14.5f, 15f, 11.5f);
            g.DrawLine(pen, 9f, 2.5f, 9f, 11f);             // стрелка вниз в лоток
            g.DrawLines(pen, new[] { new PointF(5.5f, 7.5f), new PointF(9f, 11f), new PointF(12.5f, 7.5f) });
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

        /// <summary>
        /// "Обновить дерево" (серверы/зоны/политики): маленькое дерево (корень + два
        /// потомка) + дуга обновления со стрелкой в правом нижнем углу. Придумано после
        /// ревью: старая папка не отражала сути "перечитать дерево".
        /// </summary>
        public static Bitmap TreeRefresh()
        {
            var bmp = NewCanvas(out var g);
            using var pen = new Pen(PenColor, 1.3f);
            // дерево: корень сверху, ствол, ветви, два потомка снизу
            g.DrawRectangle(pen, 2f, 2f, 5f, 3.5f);         // корневой узел
            g.DrawLine(pen, 4.5f, 5.5f, 4.5f, 8f);          // ствол
            g.DrawLine(pen, 4.5f, 8f, 2.5f, 10.5f);         // ветвь влево
            g.DrawLine(pen, 4.5f, 8f, 6.5f, 10.5f);         // ветвь вправо
            g.DrawRectangle(pen, 0.5f, 10.5f, 4f, 3.5f);    // левый потомок
            g.DrawRectangle(pen, 4.5f, 10.5f, 4f, 3.5f);    // правый потомок
            // кольцо обновления (паттерн Refresh): разрыв и стрелка сверху справа
            using var arcPen = new Pen(PenColor, 1.4f);
            g.DrawArc(arcPen, 9.5f, 1.5f, 7f, 7f, -90f, -310f);
            using var brush = new SolidBrush(PenColor);
            g.FillPolygon(brush, new[] { new PointF(14.3f, 1.1f), new PointF(16.8f, 1.8f), new PointF(14.5f, 3.7f) });
            g.Dispose();
            return bmp;
        }

        /// <summary>
        /// "Дублировать": задний лист выглядывает уголком из-за переднего + маленький "+" на
        /// переднем - читается как "сделать копии", отличается от папки/обновления рядом на той
        /// же панели. Задний лист рисуется буквой "Г" (верх + левая сторона), чтобы контуры
        /// не путались в наложении на таком мелком размере.
        /// </summary>
        public static Bitmap Duplicate()
        {
            var bmp = NewCanvas(out var g);
            using var pen = new Pen(PenColor, 1.4f);

            // задний лист - только выступающий уголок (верхняя и левая грань)
            g.DrawLine(pen, 3, 3, 10, 3);
            g.DrawLine(pen, 3, 3, 3, 11);

            // передний лист целиком поверх
            g.DrawRectangle(pen, 6, 6, 9, 9);

            // "+" в центре переднего листа
            using var plusPen = new Pen(PenColor, 1.6f);
            g.DrawLine(plusPen, 10, 8, 10, 13);
            g.DrawLine(plusPen, 8, 10, 13, 10);

            g.Dispose();
            return bmp;
        }

        /// <summary>
        /// "Добавить недостающие PTR": кольцо со стрелкой ПРОТИВ часовой (признак
        /// "обратного просмотра") + плюс в центре (создание). Один цельный глиф вместо
        /// двух разнесённых знаков - по итогам ревью.
        /// </summary>
        public static Bitmap PtrBackfill()
        {
            var bmp = NewCanvas(out var g);
            using var pen = new Pen(PenColor, 1.6f);
            // кольцо против часовой: старт справа, конец снизу справа, стрелка на конце
            g.DrawArc(pen, 2.8f, 2.8f, 11.4f, 11.4f, 355f, -300f);
            using var brush = new SolidBrush(PenColor);
            g.FillPolygon(brush, new[] { new PointF(13.6f, 11.9f), new PointF(12.6f, 14.4f), new PointF(10.9f, 11.9f) });
            using var plusPen = new Pen(PenColor, 1.7f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(plusPen, 8.5f, 6f, 8.5f, 11f);              // плюс в центре кольца
            g.DrawLine(plusPen, 6f, 8.5f, 11f, 8.5f);
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
