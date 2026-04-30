using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using Grasshopper.Kernel;
using Rhino.Geometry;

namespace BatGH
{
    public class MantaPressureComponent : GH_Component
    {
        volatile bool   _alive;
        Thread          _thread;
        DateTime        _start = DateTime.Now;

        volatile Point3d[] _sources;
        volatile double[]  _levels;
        double    _waveSpeed;
        BoundingBox _bbox;

        public MantaPressureComponent()
            : base("MN Pressure", "MN Pre",
                   "Animated acoustic pressure wavefronts radiating from noise sources.\n" +
                   "Visualises how sound energy propagates outward in time.\n" +
                   "Pairs with BT Noise — use the same sources for integrated analysis.",
                   "Analysis", "Environment")
        { }

        public override Guid ComponentGuid => new Guid("E5F6A7B8-C9D0-4234-BCEF-345678901234");
        protected override Bitmap Icon => BatIcons.MantaPre24;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddPointParameter  ("Sources",    "S",  "Noise source points (from BT Source)",       GH_ParamAccess.list);
            p.AddNumberParameter ("Levels",     "dB", "dB levels per source (from BT Source)",       GH_ParamAccess.list);
            p.AddNumberParameter ("Wave Speed", "c",  "Speed of sound in m/s (default 343)",         GH_ParamAccess.item, 343.0);
            p.AddNumberParameter ("Scale",      "Sc", "Visual scale — shrinks radius for display",   GH_ParamAccess.item, 0.05);
            p.AddIntegerParameter("Rings",      "R",  "Wavefront rings per source",                  GH_ParamAccess.item, 5);
            for (int i = 0; i < 5; i++) p[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddNumberParameter("Peak dB",    "Pk", "Peak pressure level at each source (dB)",        GH_ParamAccess.list);
            p.AddPointParameter ("Source Pts", "SP", "Source locations (pass-through)",                GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var   srcList  = new List<Point3d>();
            var   lvlList  = new List<double>();
            double speed   = 343, sc = 0.05;
            int    rings   = 5;

            if (!DA.GetDataList(0, srcList) || srcList.Count == 0)
            { AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No sources — connect BT Source"); return; }

            DA.GetDataList(1, lvlList);
            DA.GetData(2, ref speed); DA.GetData(3, ref sc); DA.GetData(4, ref rings);

            while (lvlList.Count < srcList.Count) lvlList.Add(lvlList.Count > 0 ? lvlList[lvlList.Count-1] : 80);

            _sources   = srcList.ToArray();
            _levels    = lvlList.ToArray();
            _waveSpeed = Math.Max(1, speed) * Math.Max(0.001, sc);
            rings      = Math.Max(1, Math.Min(20, rings));

            // Compute bbox from sources
            var bb = new BoundingBox(_sources);
            bb.Inflate(bb.Diagonal.Length * 0.5 + 1);
            _bbox = bb;

            DA.SetDataList(0, lvlList);
            DA.SetDataList(1, srcList);

            StartThread();
        }

        void StartThread()
        {
            if (_alive) return;
            _alive = true;
            _start = DateTime.Now;
            _thread = new Thread(() =>
            {
                while (_alive)
                {
                    Rhino.RhinoDoc.ActiveDoc?.Views.Redraw();
                    Thread.Sleep(16);
                }
            }) { IsBackground = true, Name = "MantaPressure" };
            _thread.Start();
        }

        public override void RemovedFromDocument(GH_Document doc)
        {
            _alive = false;
            base.RemovedFromDocument(doc);
        }

        public override void DrawViewportWires(IGH_PreviewArgs args)
        {
            var srcs   = _sources;
            var lvls   = _levels;
            if (srcs == null || srcs.Length == 0) return;

            double t     = (DateTime.Now - _start).TotalSeconds;
            double cVis  = _waveSpeed;
            int    nRings = 5;

            for (int si = 0; si < srcs.Length; si++)
            {
                var   src   = srcs[si];
                double level = si < lvls.Length ? lvls[si] : 80;

                // Normalise level to visual intensity (60–100 dB range)
                double intensity = Math.Max(0, Math.Min(1, (level - 50) / 50.0));

                for (int ri = 0; ri < nRings; ri++)
                {
                    // Each ring is offset in time so they appear to march outward
                    double ringT   = (t + (double)ri / nRings) % 1.0;
                    double radius  = ringT * cVis * 2.0;

                    // Fade out as ring expands and as it ages
                    double fade = (1.0 - ringT) * intensity;
                    int    alpha = (int)(200 * fade);
                    if (alpha < 5) continue;

                    // Colour: high level = warm (amber-red), low = cool (blue-cyan)
                    int r = (int)(255 * intensity + 60  * (1 - intensity));
                    int g = (int)(140 * intensity + 180 * (1 - intensity));
                    int b = (int)( 20 * intensity + 255 * (1 - intensity));
                    var col = Color.FromArgb(alpha, r, g, b);

                    // Draw 3 great-circle rings at different orientations for spherical feel
                    var planeXY = new Plane(src, Vector3d.ZAxis);
                    var planeXZ = new Plane(src, Vector3d.YAxis);
                    var planeYZ = new Plane(src, Vector3d.XAxis);
                    int thick   = ri == 0 ? 2 : 1;

                    args.Display.DrawCircle(new Circle(planeXY, radius), col, thick);
                    args.Display.DrawCircle(new Circle(planeXZ, radius), Color.FromArgb(alpha/2, r, g, b), 1);
                    args.Display.DrawCircle(new Circle(planeYZ, radius), Color.FromArgb(alpha/2, r, g, b), 1);
                }

                // Source pulse: bright core that beats with the rings
                double pulse   = 0.5 + 0.5 * Math.Sin(t * Math.PI * 2.0);
                int    pAlpha  = (int)(180 + 75 * pulse);
                double coreR   = cVis * 0.04 * (1 + 0.3 * pulse);
                var    corePl  = new Plane(src, Vector3d.ZAxis);
                args.Display.DrawCircle(new Circle(corePl, coreR),
                    Color.FromArgb(pAlpha, 255, 220, 80), 3);
                args.Display.DrawPoint(src, Rhino.Display.PointStyle.Circle, 5,
                    Color.FromArgb(255, 255, 240, 100));
            }
        }

        public override void DrawViewportMeshes(IGH_PreviewArgs args) { }
    }
}
