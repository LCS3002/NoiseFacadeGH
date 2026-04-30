using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using Grasshopper.Kernel;
using Rhino.Geometry;

namespace BatGH
{
    public class MantaSunComponent : GH_Component
    {
        volatile bool   _alive;
        Thread          _thread;
        DateTime        _start = DateTime.Now;

        // State for animated sweep
        volatile Mesh   _mesh;
        double _lat, _lon;
        volatile int    _year, _month, _day;
        double _startH, _endH, _animSpeed;
        double _elevationRef;
        BoundingBox _bbox;

        public MantaSunComponent()
            : base("MN Sun", "MN Sun",
                   "Animated solar path with real-time shadow sweep.\n" +
                   "Uses NOAA SPA algorithm — accurate to ±0.01° for 2000-2050.\n" +
                   "Connect to BT Mesh for direct integration with noise analysis.",
                   "Analysis", "Environment")
        { }

        public override Guid ComponentGuid => new Guid("D4E5F6A7-B8C9-4123-ADEF-234567890123");
        protected override Bitmap Icon => BatIcons.MantaSun24;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddMeshParameter   ("Mesh",      "M",   "Analysis mesh",                                   GH_ParamAccess.item);
            p.AddNumberParameter ("Latitude",  "Lat", "Site latitude  (° N positive)",                   GH_ParamAccess.item,  51.5);
            p.AddNumberParameter ("Longitude", "Lon", "Site longitude (° E positive)",                   GH_ParamAccess.item,  -0.1);
            p.AddIntegerParameter("Year",      "Yr",  "Year",                                            GH_ParamAccess.item, 2026);
            p.AddIntegerParameter("Month",     "Mo",  "Month (1-12)",                                    GH_ParamAccess.item,    6);
            p.AddIntegerParameter("Day",       "Dy",  "Day  (1-31)",                                     GH_ParamAccess.item,   21);
            p.AddNumberParameter ("Start Hr",  "H0",  "Analysis start (UTC hours, e.g. 6)",              GH_ParamAccess.item,  6.0);
            p.AddNumberParameter ("End Hr",    "H1",  "Analysis end   (UTC hours, e.g. 20)",             GH_ParamAccess.item, 20.0);
            p.AddNumberParameter ("Anim Spd",  "As",  "Animation speed multiplier (1 = real-time × 600)", GH_ParamAccess.item,  1.0);
            for (int i = 0; i < 9; i++) p[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddPointParameter  ("Sun Path",   "SP",  "Sun positions over the day (baked arc)",               GH_ParamAccess.list);
            p.AddVectorParameter ("Sun Dir",    "SD",  "Current animated sun direction vector",                 GH_ParamAccess.item);
            p.AddNumberParameter ("Elevation",  "El",  "Current sun elevation (°)",                            GH_ParamAccess.item);
            p.AddNumberParameter ("Azimuth",    "Az",  "Current sun azimuth (°)",                              GH_ParamAccess.item);
            p.AddNumberParameter ("Face Solar", "FS",  "Per-face solar incidence (0=shade, 1=normal incidence)", GH_ParamAccess.list);
            p.AddNumberParameter ("Peak Hours", "PH",  "Estimated peak sun hours per face",                     GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Mesh   mesh = null;
            double lat = 51.5, lon = -0.1, h0 = 6, h1 = 20, aspd = 1.0;
            int    yr = 2026, mo = 6, dy = 21;

            if (!DA.GetData(0, ref mesh) || mesh == null)
            { AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No mesh"); return; }

            DA.GetData(1, ref lat);  DA.GetData(2, ref lon);
            DA.GetData(3, ref yr);   DA.GetData(4, ref mo);  DA.GetData(5, ref dy);
            DA.GetData(6, ref h0);   DA.GetData(7, ref h1);  DA.GetData(8, ref aspd);

            mo = Math.Max(1, Math.Min(12, mo));
            dy = Math.Max(1, Math.Min(31, dy));
            if (h1 <= h0) h1 = h0 + 1;

            if (mesh.FaceNormals.Count != mesh.Faces.Count) mesh.FaceNormals.ComputeFaceNormals();

            _mesh      = mesh;
            _lat = lat; _lon = lon;
            _year = yr; _month = mo; _day = dy;
            _startH = h0; _endH = h1;
            _animSpeed = Math.Max(0.1, aspd);
            _bbox = mesh.GetBoundingBox(true);

            // Bake sun path arc
            var sunPts = new List<Point3d>();
            double bboxR = _bbox.Diagonal.Length;
            Point3d bboxCen = (Point3d)(0.5 * ((Vector3d)_bbox.Min + (Vector3d)_bbox.Max));
            double maxEl = 0;
            for (double h = h0; h <= h1; h += 0.25)
            {
                var (az, el) = MantaMath.SolarPosition(lat, lon, yr, mo, dy, h);
                if (el > 0)
                {
                    var sunDir = MantaMath.SunDirection(az, el);
                    sunPts.Add(bboxCen + sunDir * bboxR * 1.2);
                    maxEl = Math.Max(maxEl, el);
                }
            }
            _elevationRef = maxEl;

            // Per-face peak solar hours (integrate over day)
            int fc = mesh.Faces.Count;
            var peakHours = new double[fc];
            for (double h = h0; h <= h1; h += 0.5)
            {
                var (az, el) = MantaMath.SolarPosition(lat, lon, yr, mo, dy, h);
                if (el <= 0) continue;
                var sunDir = MantaMath.SunDirection(az, el);
                var frac   = MantaMath.ShadowFraction(mesh, sunDir);
                for (int fi = 0; fi < fc; fi++)
                    peakHours[fi] += frac[fi] * 0.5; // 0.5h step
            }

            // Snapshot at midday for face solar output
            var (azM, elM) = MantaMath.SolarPosition(lat, lon, yr, mo, dy, (h0+h1)/2);
            var midDir  = MantaMath.SunDirection(azM, elM);
            var midFrac = MantaMath.ShadowFraction(mesh, midDir);

            DA.SetDataList(0, sunPts);
            DA.SetData    (1, midDir);
            DA.SetData    (2, elM);
            DA.SetData    (3, azM);
            DA.SetDataList(4, midFrac);
            DA.SetDataList(5, peakHours);

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
            }) { IsBackground = true, Name = "MantaSun" };
            _thread.Start();
        }

        public override void RemovedFromDocument(GH_Document doc)
        {
            _alive = false;
            base.RemovedFromDocument(doc);
        }

        public override void DrawViewportWires(IGH_PreviewArgs args)
        {
            var mesh = _mesh;
            if (mesh == null) return;

            var bbox   = _bbox;
            double bboxR = bbox.Diagonal.Length;
            Point3d cen  = (Point3d)(0.5*((Vector3d)bbox.Min+(Vector3d)bbox.Max));

            double elapsed = (DateTime.Now - _start).TotalSeconds;
            double span    = _endH - _startH;
            double animH   = _startH + ((elapsed * _animSpeed * 0.5) % span);

            var (az, el) = MantaMath.SolarPosition(_lat, _lon, _year, _month, _day, animH);

            if (el > 0)
            {
                var sunDir = MantaMath.SunDirection(az, el);
                var sunPt  = cen + sunDir * bboxR * 1.2;

                // Sun disc glow rings
                var plane = new Plane(sunPt, -sunDir);
                for (int r = 0; r < 4; r++)
                {
                    double radius = bboxR * (0.025 + r * 0.018);
                    int    alpha  = 180 - r * 40;
                    var    circle = new Circle(plane, radius);
                    args.Display.DrawCircle(circle, Color.FromArgb(alpha, 255, 230, 80), r == 0 ? 3 : 1);
                }

                // Shadow ray lines from sun through mesh face centres
                var frac = MantaMath.ShadowFraction(mesh, sunDir);
                for (int fi = 0; fi < Math.Min(mesh.Faces.Count, 200); fi++)
                {
                    var f   = mesh.Faces[fi];
                    var fc2 = (Point3d)(((Vector3d)(Point3d)mesh.Vertices[f.A]
                                       + (Vector3d)(Point3d)mesh.Vertices[f.B]
                                       + (Vector3d)(Point3d)mesh.Vertices[f.C]) * (1.0/3.0));
                    double lit   = frac[fi];
                    int    alpha = (int)(80 * lit);
                    if (alpha < 5) continue;
                    args.Display.DrawLine(sunPt, fc2,
                        Color.FromArgb(alpha, 255, 220, 60), 1);
                }

                // Solar arc (path through the day so far)
                var arcPts = new List<Point3d>();
                for (double h = _startH; h <= animH; h += 0.2)
                {
                    var (az2, el2) = MantaMath.SolarPosition(_lat, _lon, _year, _month, _day, h);
                    if (el2 > 0) arcPts.Add(cen + MantaMath.SunDirection(az2, el2) * bboxR * 1.2);
                }
                for (int i = 0; i < arcPts.Count - 1; i++)
                {
                    double frac2 = (double)i / arcPts.Count;
                    int    alpha = (int)(160 * frac2);
                    args.Display.DrawLine(arcPts[i], arcPts[i+1],
                        Color.FromArgb(alpha, 255, 200, 50), 2);
                }
            }
        }

        public override void DrawViewportMeshes(IGH_PreviewArgs args)
        {
            var mesh = _mesh;
            if (mesh == null) return;

            double elapsed = (DateTime.Now - _start).TotalSeconds;
            double span    = _endH - _startH;
            double animH   = _startH + ((elapsed * _animSpeed * 0.5) % span);

            var (az, el) = MantaMath.SolarPosition(_lat, _lon, _year, _month, _day, animH);
            if (el <= 0) return;

            var sunDir = MantaMath.SunDirection(az, el);
            var frac   = MantaMath.ShadowFraction(mesh, sunDir);

            var dm = mesh.DuplicateMesh();
            var colors = new Color[dm.Vertices.Count];

            // Average face frac to vertices
            var sum = new double[dm.Vertices.Count];
            var cnt = new int[dm.Vertices.Count];
            for (int fi = 0; fi < dm.Faces.Count; fi++)
            {
                var f = dm.Faces[fi];
                sum[f.A] += frac[fi]; cnt[f.A]++;
                sum[f.B] += frac[fi]; cnt[f.B]++;
                sum[f.C] += frac[fi]; cnt[f.C]++;
                if (f.IsQuad) { sum[f.D] += frac[fi]; cnt[f.D]++; }
            }
            for (int vi = 0; vi < dm.Vertices.Count; vi++)
            {
                double v = cnt[vi] > 0 ? sum[vi] / cnt[vi] : 0;
                // Lit = warm yellow, shadow = cool blue-grey
                int r = (int)(255 * v + 40 * (1 - v));
                int g = (int)(220 * v + 50 * (1 - v));
                int b = (int)( 80 * v + 80 * (1 - v));
                colors[vi] = Color.FromArgb(r, g, b);
            }
            dm.VertexColors.SetColors(colors);
            args.Display.DrawMeshFalseColors(dm);
        }
    }
}
