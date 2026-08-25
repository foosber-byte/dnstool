using System.Drawing;
using System.Reflection;

namespace DnsToolWinForms
{
    /// <summary>
    /// Иконка приложения (из самого exe, см. ApplicationIcon в csproj) - один раз извлекается
    /// и переиспользуется всеми окнами (главное окно и дополнительные диалоги), чтобы у всех
    /// была одинаковая иконка в заголовке, а не только у главного окна.
    /// </summary>
    public static class AppIcon
    {
        private static Icon _cached;
        private static bool _tried;

        public static Icon Current
        {
            get
            {
                if (!_tried)
                {
                    _tried = true;
                    try
                    {
                        var exePath = Assembly.GetExecutingAssembly().Location;
                        _cached = Icon.ExtractAssociatedIcon(exePath);
                    }
                    catch
                    {
                        _cached = null; // не критично - окно останется со стандартной иконкой
                    }
                }
                return _cached;
            }
        }
    }
}
