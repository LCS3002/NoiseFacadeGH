using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using Grasshopper.Kernel;
using Rhino.Geometry;

namespace BatGH
{
    public class MantaWindComponent : GH_Component
    {
        // ── Volatile animation state ──────────────────────────────────────────
        volatile bool      _alive;
        Thread             _thread;
        DateTime           _start = DateTime.Now;

        // Set once per SolveInstance — read-only by animation thread
        volatile Point3d[] _seeds;
        Vector3d  _windDir;
        double    _windSpeed;
        double    _turbulence;
        double    _scale;
        BoundingBox _bbox;
        int       _trailSteps;

        // ── Baked streamline output (for curve output) ────────────────────────
        volatile Polyline[] _streamlines;

        public MantaWindComponent()
            : base("MN Wind", "MN Wnd",
                   "Animated wind streamlines — particles advect through a curl-noise velocity field.\n" +
                   "Set Rhino viewport to Perspective for best effect.\n" +
                   "Formula: v = V_wind + curl(N(x/scale + t·0.1, y/scale, z/scale)) × turbulence",
                   "Analysis", "Environment")
        { }

        public override Guid ComponentGuid => new Guid("C3D4E5F6-A7B8-4012-9CDE-123456789012");
        protected override Bitmap Icon => BatIcons.MantaWind24;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddMeshParameter   ("Mesh",       "M",  "Analysis mesh (e.g. from BT Mesh)",           GH_ParamAccess.item);
            p.AddVectorParameter ("Wind Dir",   "V",  "Wind direction vector (will be normalised)",   GH_ParamAccess.item, new Vector3d(1, 0, 0));
            p.AddNumberParameter ("Speed",      "Sp", "Wind speed — controls animation rate",         GH_ParamAccess.item, 5.0);
            p.AddNumberParameter ("Turbulence", "Tu", "Curl-noise turbulence intensity (0 = laminar)", GH_ParamAccess.item, 1.5);
            p.AddNumberParameter ("Scale",      "Sc", "Noise scale relative to geometry",             GH_ParamAccess.item, 10.0);
            p.AddIntegerParameter("Particles",  "N",  "Number of streamline particles",               GH_ParamAccess.item, 80);
            p.AddIntegerParameter("Trail",      "Tr", "Trail length (steps)",                         GH_ParamAccess.item, 20);
            p.AddIntegerParameter("Seed",       "S",  "Random seed for particle placement",           GH_ParamAccess.item, 0);
            for (int i = 0; i < 8; i++) p[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddCurveParameter ("Streamlines", "SL", "Baked streamline polylines — connect to see static field", GH_ParamAccess.list);
            p.AddVectorParameter("Field Pts",   "FP", "Sampled wind vectors at mesh face centres",               GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Mesh    mesh   = null;
            var     dir    = new Vector3d(1, 0, 0);
            double  speed  = 5, turb = 1.5, scale = 10;
            int     nPart  = 80, trail = 20, seed = 0;

            if (!DA.GetData(0, ref mesh) || mesh == null)
            { AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No mesh"); return; }

            DA.GetData(1, ref dir);   DA.GetData(2, ref speed);
            DA.GetData(3, ref turb);  DA.GetData(4, ref scale);
            DA.GetData(5, ref nPart); DA.GetData(6, ref trail);
            DA.GetData(7, ref seed);

            nPart = Math.Max(1, Math.Min(500, nPart));
            trail = Math.Max(2, Math.Min(100, trail));
            speed = Math.Max(0.1, speed);
            scale = Math.Max(0.1, scale);

            dir.Unitize();

            // Store for animation thread
            _windDir    = dir;
            _windSpeed  = speed;
            _turbulence = turb;
            _scale      = scale;
            _trailSteps = trail;
            _bbox       = mesh.GetBoundingBox(true);
            _bbox.Inflate(_bbox.Diagonal.Length * 0.3);
            _seeds      = MantaMath.SeedParticles(_bbox, dir, nPart, seed);

            // Bake a static streamline snapshot for the curve output
            double dt  = _bbox.Diagonal.Length / (speed * 40);
            var    sls = new Polyline[nPart];
            for (int i = 0; i < nPart; i++)
            {
                var poly = new Polyline();
                var pt   = _seeds[i];
                for (int s2 = 0; s2 < trail * 4; s2++)
                {
                    poly.Add(pt);
                    pt = MantaMath.Advect(pt, dir, turb, scale, 0, dt);
                    if (!_bbox.Contains(pt)) break;
                }
                sls[i] = poly;
            }
            _streamlines = sls;

            // Wind vectors at face centres
            var fieldVecs = new List<Vector3d>();
            if (mesh.FaceNormals.Count != mesh.Faces.Count)
                mesh.FaceNormals.ComputeFaceNormals();
            foreach (var f in mesh.Faces)
            {
                var c = (Point3d)(((Vector3d)(Point3d)mesh.Vertices[f.A]
                                 + (Vector3d)(Point3d)mesh.Vertices[f.B]
                                 + (Vector3d)(Point3d)mesh.Vertices[f.C]) * (1.0/3.0));
                fieldVecs.Add(MantaMath.WindVelocity(c, dir, turb, scale, 0));
            }

            DA.SetDataList(0, new List<Curve>(Array.ConvertAll(sls, sl => (Curve)sl.ToNurbsCurve())));
            DA.SetDataList(1, fieldVecs);

            StartThread();
        }

        // ── Animation thread ──────────────────────────────────────────────────
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
                    Thread.Sleep(16); // ~60 fps
                }
            }) { IsBackground = true, Name = "MantaWind" };
            _thread.Start();
        }

        public override void RemovedFromDocument(GH_Document doc)
        {
            _alive = false;
            base.RemovedFromDocument(doc);
        }

        // ── Viewport drawing ──────────────────────────────────────────────────
        public override void DrawViewportWires(IGH_PreviewArgs args)
        {
            var    seeds  = _seeds;
            if (seeds == null) return;

            var    dir    = _windDir;
            double speed  = _windSpeed;
            double turb   = _turbulence;
            double sc     = _scale;
            var    bbox   = _bbox;
            int    trail  = _trailSteps;

            double t     = (DateTime.Now - _start).TotalSeconds;
            double bboxL = bbox.Diagonal.Length;
            double dt    = bboxL / (speed * 40);

            for (int i = 0; i < seeds.Length; i++)
            {
                // Phase offset so particles spread along the path
                double phase = (i * 0.618033988) % 1.0; // golden ratio spacing
                double tOff  = (t * speed * 0.07 + phase) % 1.0;

                // Start particle at seed position, offset downstream by tOff
                Point3d head = seeds[i] + dir * (tOff * bboxL * 0.8);

                // Wrap back if outside bbox
                int wrap = 0;
                while (!bbox.Contains(head) && wrap++ < 3)
                    head = head - dir * bboxL * 0.8;

                // Build trail by back-integrating
                var trail_pts = new Point3d[trail];
                trail_pts[0] = head;
                double simT = t * speed * 0.07 + phase;
                for (int s = 1; s < trail; s++)
                {
                    trail_pts[s] = MantaMath.Advect(trail_pts[s-1], dir, turb, sc,
                                                    -simT - s*dt*speed*0.3, -dt);
                }

                // Draw trail segments with fading colour
                for (int s = 0; s < trail - 1; s++)
                {
                    double frac  = 1.0 - (double)s / trail;
                    int    alpha = (int)(220 * frac * frac);
                    int    blue  = (int)(255 * frac);
                    int    green = (int)(210 * frac);
                    var    col   = Color.FromArgb(alpha, 80, green, blue);
                    args.Display.DrawLine(trail_pts[s], trail_pts[s+1], col, 1);
                }

                // Bright head dot
                if (bbox.Contains(trail_pts[0]))
                    args.Display.DrawPoint(trail_pts[0],
                        Rhino.Display.PointStyle.Circle, 3,
                        Color.FromArgb(220, 160, 240, 255));
            }
        }

        public override void DrawViewportMeshes(IGH_PreviewArgs args) { }
    }
}
