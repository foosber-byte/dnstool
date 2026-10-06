using System;
using System.Drawing;
using System.Windows.Forms;

namespace DnsToolWinForms.Controls
{
    /// <summary>
    /// Общие хелперы разметки, одинаковые для MainForm и всех UserControl'ов вкладок:
    /// Row/Column (FlowLayoutPanel-строки), Tb (TextBox с placeholder, как в net5+),
    /// Val (чтение значения мимо placeholder) и SetPlaceholder.
    /// </summary>
    public static class Ui
    {
        /// <summary>Имя ЭТОГО компьютера в верхнем регистре - подпись узла локального сервера в дереве и выпадашках.</summary>
        public static string LocalServerLabel() => Environment.MachineName.ToUpperInvariant();

        /// <summary>
        /// Раскладка "колонка управления сверху + две панели рядом (бок о бок), разделённые
        /// перетаскиваемой границей" - например слева список имён, справа подробности
        /// выбранного элемента. Позиция границы запоминается в settings.ini под ключом
        /// splitterSettingsKey и восстанавливается при следующем запуске.
        /// </summary>
        public static TableLayoutPanel TwoListsLayout(Control controlsColumn,
            string leftTitle, Control leftList, string rightTitle, Control rightList,
            string splitterSettingsKey)
        {
            var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            controlsColumn.Dock = DockStyle.Top;

            var split = new SplitContainer
            {
                // Явно задаём стартовый размер ДО min-size свойств: у только что созданного
                // SplitContainer размер по умолчанию маленький (~150px), и если сумма
                // Panel1MinSize+Panel2MinSize+SplitterWidth больше этого стартового размера,
                // WinForms бросает InvalidOperationException прямо здесь же, при инициализации -
                // до Dock=Fill и до любых try/catch в обработчиках событий.
                Size = new Size(800, 500),
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical, // граница - вертикальная линия, панели слева/справа
                SplitterWidth = 6,
                Panel1MinSize = 100,
                Panel2MinSize = 120
            };

            leftList.Dock = DockStyle.Fill;
            rightList.Dock = DockStyle.Fill;
            if (leftList is ListBox leftLb) { leftLb.Font = new Font("Consolas", 9F); leftLb.HorizontalScrollbar = true; }
            if (rightList is ListBox rightLb) { rightLb.Font = new Font("Consolas", 9F); rightLb.HorizontalScrollbar = true; }

            var leftPanel = new Panel { Dock = DockStyle.Fill };
            leftPanel.Controls.Add(leftList);
            leftPanel.Controls.Add(new Label { Text = leftTitle, Dock = DockStyle.Top, Height = 22, Font = new Font("Segoe UI", 9F, FontStyle.Bold), Padding = new Padding(2, 4, 2, 2) });

            var rightPanel = new Panel { Dock = DockStyle.Fill };
            rightPanel.Controls.Add(rightList);
            rightPanel.Controls.Add(new Label { Text = rightTitle, Dock = DockStyle.Top, Height = 22, Font = new Font("Segoe UI", 9F, FontStyle.Bold), Padding = new Padding(2, 4, 2, 2) });

            split.Panel1.Controls.Add(leftPanel);
            split.Panel2.Controls.Add(rightPanel);

            // Восстанавливаем сохранённую позицию границы; если сохранённое значение уже не
            // помещается (например окно стало у́же) - SplitContainer сам подберёт ближайшее
            // валидное, ошибка тут не критична и не должна ронять приложение.
            split.HandleCreated += (s, e) =>
            {
                try { split.SplitterDistance = AppSettings.GetInt(splitterSettingsKey, 380); }
                catch { /* сохранённое значение больше не подходит по размеру - оставляем как есть */ }
            };
            split.SplitterMoved += (s, e) => AppSettings.SetInt(splitterSettingsKey, split.SplitterDistance);

            table.Controls.Add(controlsColumn, 0, 0);
            table.Controls.Add(split, 0, 1);
            return table;
        }

        public static FlowLayoutPanel Row(params Control[] controls)
        {
            var p = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                WrapContents = false,
                Margin = new Padding(0, 0, 0, 2)
            };
            foreach (var c in controls)
            {
                c.Margin = new Padding(4, 6, 4, 2);
                p.Controls.Add(c);
            }
            return p;
        }

        public static FlowLayoutPanel Column(params Control[] rows)
        {
            var p = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                AutoSize = true,
                WrapContents = false,
                Dock = DockStyle.Top
            };
            foreach (var r in rows)
            {
                r.Margin = new Padding(0);
                p.Controls.Add(r);
            }
            return p;
        }

        // .NET Framework 4.8 не знает TextBox.PlaceholderText (это фича WinForms из .NET 5+),
        // поэтому имитируем placeholder вручную: серый текст-подсказка, которая исчезает по фокусу.
        public static TextBox Tb(int width = 200, string placeholderText = null)
        {
            var t = new TextBox { Width = width };
            if (!string.IsNullOrEmpty(placeholderText))
            {
                t.Tag = placeholderText;
                t.Text = placeholderText;
                t.ForeColor = Color.Gray;

                t.Enter += (s, e) =>
                {
                    if (t.ForeColor == Color.Gray)
                    {
                        t.Text = "";
                        t.ForeColor = SystemColors.WindowText;
                    }
                };
                t.Leave += (s, e) =>
                {
                    if (string.IsNullOrWhiteSpace(t.Text))
                    {
                        // Порядок важен: ForeColor выставляем ДО Text, потому что TextChanged
                        // срабатывает синхронно на присвоение Text, а слушатели (например у поля
                        // "Целевой сервер") должны увидеть серый цвет уже в момент события,
                        // иначе placeholder ошибочно прочитается как настоящее значение.
                        t.ForeColor = Color.Gray;
                        t.Text = (string)t.Tag;
                    }
                };
            }
            return t;
        }

        /// <summary>
        /// Читает реальное значение TextBox - если сейчас показан серый placeholder,
        /// возвращает "" вместо текста подсказки.
        /// </summary>
        public static string Val(TextBox t)
        {
            if (t.Tag is string placeholder && t.ForeColor == Color.Gray && t.Text == placeholder)
                return "";
            return t.Text.Trim();
        }

        public static string Val(ComboBox c) => (c.Text ?? "").Trim();

        /// <summary>Меняет текст-подсказку у уже созданного поля (используется при смене типа записи).</summary>
        public static void SetPlaceholder(TextBox t, string newPlaceholder)
        {
            var wasShowingPlaceholder = t.Tag is string oldPh && t.ForeColor == Color.Gray && t.Text == oldPh;
            t.Tag = newPlaceholder;
            if (wasShowingPlaceholder || string.IsNullOrEmpty(t.Text))
            {
                t.Text = newPlaceholder;
                t.ForeColor = Color.Gray;
            }
        }
    }
}
