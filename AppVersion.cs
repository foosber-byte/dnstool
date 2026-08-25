namespace DnsToolWinForms
{
    /// <summary>
    /// Единственное место, где хранится текущая версия - при релизе меняешь только здесь
    /// (плюс AssemblyTitle в .csproj для метаданных exe). Заголовок окна и "О программе"
    /// берут версию отсюда, а не хранят своё отдельное значение.
    /// </summary>
    public static class AppVersion
    {
        public const string Current = "2.3.1";
    }
}
