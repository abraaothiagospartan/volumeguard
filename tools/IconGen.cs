// Gera assets\app.ico a partir do desenho em src\UI\IconArt.cs (chamado pelo build.ps1).
class IconGen
{
    static void Main(string[] args)
    {
        VolumeGuard.UI.IconArt.WriteIco(args[0], new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 }, VolumeGuard.UI.IconArt.AppColors);
    }
}
