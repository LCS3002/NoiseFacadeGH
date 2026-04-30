using System;
using System.Drawing;
using System.Reflection;
using Grasshopper.Kernel;

namespace BatGH
{
    static class BatColors
    {
        public static readonly Color Navy  = Color.FromArgb( 10,  14,  32);
        public static readonly Color Amber = Color.FromArgb(245, 166,  35);
    }

    static class BatIcons
    {
        static Bitmap Load(string name)
        {
            try
            {
                using (var s = Assembly.GetExecutingAssembly()
                                       .GetManifestResourceStream("BatGH." + name))
                    return s != null ? new Bitmap(s) : null;
            }
            catch { return null; }
        }

        static Bitmap _bat24, _bat48, _src24, _msh24, _nse24, _int24, _con24, _leg24;
        static Bitmap _mwnd24, _msun24, _mpre24;

        public static Bitmap Bat24        => _bat24  ?? (_bat24  = Load("Bat_24.png"));
        public static Bitmap Bat48        => _bat48  ?? (_bat48  = Load("Bat_48.png"));
        public static Bitmap Source24     => _src24  ?? (_src24  = Load("BatSource_24.png"));
        public static Bitmap Mesh24       => _msh24  ?? (_msh24  = Load("BatMesh_24.png"));
        public static Bitmap Noise24      => _nse24  ?? (_nse24  = Load("BatNoise_24.png"));
        public static Bitmap Interior24   => _int24  ?? (_int24  = Load("BatInterior_24.png"));
        public static Bitmap Contours24   => _con24  ?? (_con24  = Load("BatContours_24.png"));
        public static Bitmap Legend24     => _leg24  ?? (_leg24  = Load("BatLegend_24.png"));
        public static Bitmap MantaWind24  => _mwnd24 ?? (_mwnd24 = Load("MantaWind_24.png"));
        public static Bitmap MantaSun24   => _msun24 ?? (_msun24 = Load("MantaSun_24.png"));
        public static Bitmap MantaPre24   => _mpre24 ?? (_mpre24 = Load("MantaPressure_24.png"));
    }

    public class BatAssemblyInfo : GH_AssemblyInfo
    {
        public override string Name          => "Bat GH";
        public override string Description   => "Acoustic site analysis — direct + reflected noise, isodecibel contours, interior optimisation. Bat = echolocation = acoustic ray tracing.";
        public override Guid   Id            => new Guid("A1B2C3D4-E5F6-4890-8BCD-EF1234567891");
        public override string AuthorName    => "Bat GH";
        public override string AuthorContact => "https://github.com/LCS3002/NoiseFacadeGH";
        public override Bitmap Icon          => BatIcons.Bat48;
    }
}
