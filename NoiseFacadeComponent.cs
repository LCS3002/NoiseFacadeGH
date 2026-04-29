using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using Grasshopper.Kernel;
using Rhino.Geometry;

namespace NoiseFacadeGH
{
    static class Icons
    {
        static Bitmap _icon24, _icon48;

        public static Bitmap Icon24 => _icon24 ?? (_icon24 = Load("NoiseFacadeGH.NoiseFacade_24.png"));
        public static Bitmap Icon48 => _icon48 ?? (_icon48 = Load("NoiseFacadeGH.NoiseFacade_48.png"));

        static Bitmap Load(string resource)
        {
            try
            {
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
                    return s != null ? new Bitmap(s) : null;
            }
            catch { return null; }
        }
    }

    public class NoiseFacadeInfo : GH_AssemblyInfo
    {
        public override string Name          => "NoiseFacade GH";
        public override string Description   => "Acoustic noise heat-map on architectural geometry";
        public override Guid   Id            => new Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567890");
        public override string AuthorName    => "NoiseFacade";
        public override string AuthorContact => "https://github.com/lorenz/NoiseFacadeGH";
        public override Bitmap Icon          => Icons.Icon48;
    }

    public class NoiseFacadeComponent : GH_Component
    {
        // volatile so the Draw thread always sees the latest reference
        private volatile Mesh _preview;
        // offset copy used only for drawing — avoids z-fighting with source geometry
        private volatile Mesh _displayMesh;

        public NoiseFacadeComponent()
            : base("NoiseFacade", "NFacade",
                   "Paints acoustic noise levels on facade geometry.\n" +
                   "Inverse-square law + Lambert cosine, multi-source energy summation.",
                   "Analysis", "Acoustic")
        { }

        public override Guid ComponentGuid => new Guid("B2C3D4E5-F6A7-8901-BCDE-F12345678901");

        protected override Bitmap Icon => Icons.Icon24;

        // ---- inputs / outputs -----------------------------------------------

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGeometryParameter("Geometry", "G",
                "Facade geometry – Mesh, Surface, Brep, SubD or Extrusion",
                GH_ParamAccess.item);
            p.AddPointParameter("Sources", "S",
                "Noise source points (one per dB level)",
                GH_ParamAccess.list);
            p.AddNumberParameter("dB Levels", "dB",
                "Sound power level at each source in dB SPL",
                GH_ParamAccess.list);
            p.AddIntegerParameter("Quality", "Q",
                "Mesh quality: 0 = fast render, 1 = default, 2 = analysis, 3 = fine",
                GH_ParamAccess.item, 1);
            p.AddNumberParameter("Min dB", "Min",
                "Lower bound of colour scale (auto if omitted)",
                GH_ParamAccess.item);
            p.AddNumberParameter("Max dB", "Max",
                "Upper bound of colour scale (auto if omitted)",
                GH_ParamAccess.item);
            p[3].Optional = true;
            p[4].Optional = true;
            p[5].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddMeshParameter  ("Mesh",    "M",   "Vertex-coloured mesh",      GH_ParamAccess.item);
            p.AddNumberParameter("Face dB", "dB",  "Per-face dB values",        GH_ParamAccess.list);
            p.AddNumberParameter("Min dB",  "Min", "Actual scale minimum (dB)", GH_ParamAccess.item);
            p.AddNumberParameter("Max dB",  "Max", "Actual scale maximum (dB)", GH_ParamAccess.item);
        }

        // ---- solve ----------------------------------------------------------

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // FIX: clear stale preview so it doesn't linger on error/disconnect
            _preview     = null;
            _displayMesh = null;

            GeometryBase geo = null;
            var sources = new List<Point3d>();
            var levels  = new List<double>();
            int quality = 1;
            double userMin = double.NaN, userMax = double.NaN;

            if (!DA.GetData(0, ref geo) || geo == null)
            { AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No geometry"); return; }

            if (!DA.GetDataList(1, sources) || sources.Count == 0)
            { AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No source points"); return; }

            if (!DA.GetDataList(2, levels) || levels.Count == 0)
            { AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No dB levels"); return; }

            DA.GetData(3, ref quality);
            quality = Math.Max(0, Math.Min(3, quality));

            bool hasMin = DA.GetData(4, ref userMin);
            bool hasMax = DA.GetData(5, ref userMax);

            // pad levels list to match sources
            while (levels.Count < sources.Count)
                levels.Add(levels[levels.Count - 1]);

            Mesh mesh = ConvertToMesh(geo, quality);
            if (mesh == null)
            { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Could not convert geometry to mesh"); return; }

            // FIX: guard against 0-face mesh (LINQ Min/Max throw on empty sequence)
            if (mesh.Faces.Count == 0)
            { AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Mesh has no faces"); return; }

            mesh.FaceNormals.ComputeFaceNormals();
            mesh.Normals.ComputeNormals();

            int fc = mesh.Faces.Count;
            var faceDb = new double[fc];
            for (int fi = 0; fi < fc; fi++)
            {
                faceDb[fi] = ComputeFaceDb(
                    FaceCentroid(mesh, mesh.Faces[fi]),
                    FaceNormal(mesh, fi),
                    sources, levels);
            }

            double scaleMin = (hasMin && !double.IsNaN(userMin)) ? userMin : faceDb.Min();
            double scaleMax = (hasMax && !double.IsNaN(userMax)) ? userMax : faceDb.Max();
            if (Math.Abs(scaleMax - scaleMin) < 1e-6) scaleMax = scaleMin + 1.0;

            PaintVertices(mesh, faceDb, scaleMin, scaleMax);

            _preview     = mesh;
            _displayMesh = BuildDisplayMesh(mesh);

            DA.SetData    (0, mesh);
            DA.SetDataList(1, faceDb);
            DA.SetData    (2, scaleMin);
            DA.SetData    (3, scaleMax);
        }

        // ---- viewport preview -----------------------------------------------

        public override void DrawViewportMeshes(IGH_PreviewArgs args)
        {
            var m = _displayMesh;
            if (m != null && m.VertexColors.Count > 0)
                args.Display.DrawMeshFalseColors(m);
        }

        public override void DrawViewportWires(IGH_PreviewArgs args)
        {
            var m = _displayMesh;
            if (m != null)
                args.Display.DrawMeshWires(m, args.WireColour);
        }

        // push every vertex outward along its normal by 0.1% of bounding-box diagonal
        // so the display mesh never z-fights with the source geometry
        static Mesh BuildDisplayMesh(Mesh src)
        {
            var m = src.DuplicateMesh();
            double offset = src.GetBoundingBox(false).Diagonal.Length * 0.001;
            if (offset < 1e-6) offset = 1e-6;
            for (int i = 0; i < m.Vertices.Count; i++)
            {
                var n = m.Normals[i];
                var v = (Point3d)m.Vertices[i];
                v += new Vector3d(n.X, n.Y, n.Z) * offset;
                m.Vertices[i] = new Point3f((float)v.X, (float)v.Y, (float)v.Z);
            }
            return m;
        }

        // ---- geometry conversion --------------------------------------------

        static Mesh ConvertToMesh(GeometryBase geo, int quality)
        {
            var mp = quality == 0 ? MeshingParameters.FastRenderMesh
                   : quality == 1 ? MeshingParameters.Default
                   : quality == 2 ? MeshingParameters.DefaultAnalysisMesh
                                  : MeshingParameters.QualityRenderMesh;

            if (geo is Mesh directMesh)
                return directMesh.DuplicateMesh();

            Brep brep = null;

            if (geo is Surface srf)
            {
                brep = srf.ToBrep();
            }
            else if (geo is Brep b)
            {
                brep = b;
            }
            else if (geo is Extrusion ext)
            {
                brep = ext.ToBrep(false);
            }
            else if (geo is SubD subd)
            {
                try
                {
                    brep = subd.ToBrep(new SubDToBrepOptions());
                }
                catch
                {
                    // FIX: null-check CreateFromSubD result before Append
                    var sm = Mesh.CreateFromSubD(subd, 3);
                    if (sm != null && sm.Faces.Count > 0) return sm;
                }
            }

            if (brep != null)
            {
                var arr = Mesh.CreateFromBrep(brep, mp);
                if (arr != null && arr.Length > 0)
                {
                    var combined = new Mesh();
                    foreach (var m in arr) combined.Append(m);
                    return combined;
                }
            }
            return null;
        }

        // ---- acoustic maths -------------------------------------------------

        // L = L_src - 20·log10(d) - 11 + 10·log10(cosθ + 0.01)
        // multi-source: 10·log10(Σ 10^(Li/10))
        static double ComputeFaceDb(Point3d centroid, Vector3d normal,
                                    List<Point3d> sources, List<double> levels)
        {
            double energySum = 0.0;
            for (int i = 0; i < sources.Count; i++)
            {
                var dir = sources[i] - centroid;
                double d = Math.Max(dir.Length, 0.1);
                dir.Unitize(); // safe: returns false and leaves dir=(0,0,0) if zero-length
                double cosTheta = Math.Max(Vector3d.Multiply(normal, dir), 0.0);
                double L = levels[i]
                           - 20.0 * Math.Log10(d)
                           - 11.0
                           + 10.0 * Math.Log10(cosTheta + 0.01);
                energySum += Math.Pow(10.0, L / 10.0);
            }
            if (energySum <= 0.0) return -200.0;
            return 10.0 * Math.Log10(energySum);
        }

        // ---- geometry helpers -----------------------------------------------

        static Point3d FaceCentroid(Mesh mesh, MeshFace f)
        {
            var vA = (Point3d)mesh.Vertices[f.A];
            var vB = (Point3d)mesh.Vertices[f.B];
            var vC = (Point3d)mesh.Vertices[f.C];
            if (f.IsQuad)
            {
                var vD = (Point3d)mesh.Vertices[f.D];
                return new Point3d(
                    (vA.X + vB.X + vC.X + vD.X) * 0.25,
                    (vA.Y + vB.Y + vC.Y + vD.Y) * 0.25,
                    (vA.Z + vB.Z + vC.Z + vD.Z) * 0.25);
            }
            return new Point3d(
                (vA.X + vB.X + vC.X) / 3.0,
                (vA.Y + vB.Y + vC.Y) / 3.0,
                (vA.Z + vB.Z + vC.Z) / 3.0);
        }

        static Vector3d FaceNormal(Mesh mesh, int fi)
        {
            var nf = mesh.FaceNormals[fi];
            var n  = new Vector3d(nf.X, nf.Y, nf.Z);
            n.Unitize();
            return n;
        }

        // ---- colour --------------------------------------------------------

        // gradient: blue → cyan → yellow → orange → red
        static readonly double[] StopT = { 0.00, 0.25, 0.50, 0.75, 1.00 };
        static readonly int[]    StopR = {    0,    0,  255,  255,  255 };
        static readonly int[]    StopG = {    0,  255,  255,  128,    0 };
        static readonly int[]    StopB = {  255,  255,    0,    0,    0 };

        static Color DbToColor(double db, double minDb, double maxDb)
        {
            double t = (db - minDb) / (maxDb - minDb);
            t = t < 0.0 ? 0.0 : t > 1.0 ? 1.0 : t;
            for (int i = 0; i < StopT.Length - 1; i++)
            {
                if (t <= StopT[i + 1])
                {
                    double seg = (t - StopT[i]) / (StopT[i + 1] - StopT[i]);
                    return Color.FromArgb(
                        (int)(StopR[i] + seg * (StopR[i + 1] - StopR[i])),
                        (int)(StopG[i] + seg * (StopG[i + 1] - StopG[i])),
                        (int)(StopB[i] + seg * (StopB[i + 1] - StopB[i])));
                }
            }
            return Color.FromArgb(255, 0, 0);
        }

        // per-vertex db = average of incident face dBs for smooth gradient
        static void PaintVertices(Mesh mesh, double[] faceDb, double scaleMin, double scaleMax)
        {
            int vc = mesh.Vertices.Count;
            var sum   = new double[vc];
            var cnt   = new int[vc];

            for (int fi = 0; fi < mesh.Faces.Count; fi++)
            {
                var f  = mesh.Faces[fi];
                double db = faceDb[fi];
                sum[f.A] += db; cnt[f.A]++;
                sum[f.B] += db; cnt[f.B]++;
                sum[f.C] += db; cnt[f.C]++;
                if (f.IsQuad) { sum[f.D] += db; cnt[f.D]++; }
            }

            var colors = new Color[vc];
            for (int vi = 0; vi < vc; vi++)
            {
                double avg = cnt[vi] > 0 ? sum[vi] / cnt[vi] : scaleMin;
                colors[vi] = DbToColor(avg, scaleMin, scaleMax);
            }
            mesh.VertexColors.SetColors(colors);
        }
    }
}
