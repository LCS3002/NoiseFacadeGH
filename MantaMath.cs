using System;
using System.Collections.Generic;
using Rhino.Geometry;

namespace BatGH
{
    // ── Shared math for all Manta Engine components ───────────────────────────
    static class MantaMath
    {
        // ── Curl noise for organic-looking streamlines ────────────────────────
        // Simple smooth noise via value noise + analytical curl
        static double Fade(double t) => t * t * t * (t * (t * 6 - 15) + 10);
        static double Lerp(double a, double b, double t) => a + t * (b - a);

        static double Noise3(double x, double y, double z)
        {
            int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y), iz = (int)Math.Floor(z);
            double fx = x - ix, fy = y - iy, fz = z - iz;
            double u = Fade(fx), v = Fade(fy), w = Fade(fz);

            double g(int xi, int yi, int zi)
            {
                int h = (xi * 1619 + yi * 31337 + zi * 6271 + 1013904223) & 0x7FFFFFFF;
                return (h & 1) == 0
                    ? Lerp(-1, 1, (double)(h >> 1 & 0x7FFF) / 0x7FFF)
                    : Lerp(-1, 1, (double)(h >> 1 & 0x7FFF) / 0x7FFF);
            }

            return Lerp(
                Lerp(Lerp(g(ix,iy,iz),   g(ix+1,iy,iz),   u),
                     Lerp(g(ix,iy+1,iz), g(ix+1,iy+1,iz), u), v),
                Lerp(Lerp(g(ix,iy,iz+1),   g(ix+1,iy,iz+1),   u),
                     Lerp(g(ix,iy+1,iz+1), g(ix+1,iy+1,iz+1), u), v),
                w);
        }

        // Curl of a noise potential field — gives divergence-free velocity
        // dF/dy(Nz) − dF/dz(Ny), etc.  (finite-difference approximation)
        public static Vector3d CurlNoise(double x, double y, double z, double eps = 0.01)
        {
            double dNz_dy = (Noise3(x, y+eps, z) - Noise3(x, y-eps, z)) / (2*eps);
            double dNy_dz = (Noise3(x, y, z+eps) - Noise3(x, y, z-eps)) / (2*eps);
            double dNx_dz = (Noise3(x, y, z+eps) - Noise3(x, y, z-eps)) / (2*eps);
            double dNz_dx = (Noise3(x+eps, y, z) - Noise3(x-eps, y, z)) / (2*eps);
            double dNy_dx = (Noise3(x+eps, y, z) - Noise3(x-eps, y, z)) / (2*eps);
            double dNx_dy = (Noise3(x, y+eps, z) - Noise3(x, y-eps, z)) / (2*eps);

            return new Vector3d(dNz_dy - dNy_dz, dNx_dz - dNz_dx, dNy_dx - dNx_dy);
        }

        // ── Wind field: base wind + curl turbulence ───────────────────────────
        public static Vector3d WindVelocity(Point3d pt, Vector3d baseWind,
                                            double turbulence, double scale, double time)
        {
            double ox = pt.X / scale + time * 0.1;
            double oy = pt.Y / scale;
            double oz = pt.Z / scale;
            Vector3d curl = CurlNoise(ox, oy, oz);
            return baseWind + curl * turbulence;
        }

        // ── Particle advection step (RK2) ────────────────────────────────────
        public static Point3d Advect(Point3d pt, Vector3d baseWind,
                                     double turbulence, double scale, double time, double dt)
        {
            Vector3d k1 = WindVelocity(pt,              baseWind, turbulence, scale, time);
            Vector3d k2 = WindVelocity(pt + k1*(dt/2),  baseWind, turbulence, scale, time + dt/2);
            return pt + k2 * dt;
        }

        // ── Particle seed: upstream face of bounding box ─────────────────────
        public static Point3d[] SeedParticles(BoundingBox bbox, Vector3d windDir,
                                              int count, int seed)
        {
            var rng = new Random(seed);
            var pts = new Point3d[count];
            Vector3d up   = Math.Abs(windDir.Z) < 0.9 ? new Vector3d(0, 0, 1) : new Vector3d(1, 0, 0);
            Vector3d side = Vector3d.CrossProduct(windDir, up);
            side.Unitize();
            up = Vector3d.CrossProduct(side, windDir);
            up.Unitize();

            // Upstream centre: bbox centre − windDir * half extent
            Point3d centre = (Point3d)(0.5 * ((Vector3d)bbox.Min + (Vector3d)bbox.Max));
            double ext = bbox.Diagonal.Length * 0.6;
            Point3d upstreamCentre = centre - windDir * ext;

            double spread = bbox.Diagonal.Length * 0.55;
            for (int i = 0; i < count; i++)
            {
                double u = (rng.NextDouble() - 0.5) * spread;
                double v = (rng.NextDouble() - 0.5) * spread;
                pts[i] = upstreamCentre + side * u + up * v;
            }
            return pts;
        }

        // ── Solar position (NOAA simplified SPA) ─────────────────────────────
        // Returns (azimuth °, elevation °) — both 0 when below horizon
        public static (double az, double el) SolarPosition(
            double latDeg, double lonDeg,
            int year, int month, int day, double hourUtc)
        {
            // Julian day number
            double A = Math.Floor((14.0 - month) / 12);
            double Y = year + 4800 - A;
            double M = month + 12 * A - 3;
            double JD = day + Math.Floor((153*M+2)/5) + 365*Y
                       + Math.Floor(Y/4) - Math.Floor(Y/100) + Math.Floor(Y/400)
                       - 32045 + hourUtc / 24.0 - 0.5;

            double T  = (JD - 2451545.0) / 36525.0;

            // Geometric mean longitude + anomaly
            double L0 = (280.46646 + T*(36000.76983 + T*0.0003032)) % 360;
            double M0 = 357.52911 + T*(35999.05029 - 0.0001537*T);
            double Mr = M0 * Math.PI / 180;

            // Equation of centre
            double C = (1.914602 - T*(0.004817+0.000014*T))*Math.Sin(Mr)
                     + (0.019993 - 0.000101*T)*Math.Sin(2*Mr)
                     + 0.000289*Math.Sin(3*Mr);

            double sunLon  = L0 + C;
            double omega   = 125.04 - 1934.136*T;
            double lambdaD = sunLon - 0.00569 - 0.00478*Math.Sin(omega * Math.PI/180);
            double lambdaR = lambdaD * Math.PI / 180;

            // Obliquity
            double eps0 = 23.0 + (26.0 + (21.448 - T*(46.8150 + T*(0.00059 - T*0.001813)))/60)/60;
            double epsR = (eps0 + 0.00256*Math.Cos(omega * Math.PI/180)) * Math.PI / 180;

            // Declination + right ascension
            double decl = Math.Asin(Math.Sin(epsR)*Math.Sin(lambdaR)) * 180/Math.PI;
            double RA   = Math.Atan2(Math.Cos(epsR)*Math.Sin(lambdaR), Math.Cos(lambdaR)) * 180/Math.PI;

            // Equation of time (minutes)
            double y2   = Math.Pow(Math.Tan(epsR/2), 2);
            double L0r  = L0 * Math.PI / 180;
            double e    = 0.016708634 - T*(0.000042037 + 0.0000001267*T);
            double EqT  = 4 * 180/Math.PI * (y2*Math.Sin(2*L0r)
                          - 2*e*Math.Sin(Mr)
                          + 4*e*y2*Math.Sin(Mr)*Math.Cos(2*L0r)
                          - 0.5*y2*y2*Math.Sin(4*L0r)
                          - 1.25*e*e*Math.Sin(2*Mr));

            // True solar time
            double TST = (hourUtc*60 + EqT + 4*lonDeg) % 1440;
            double HA  = TST/4 < 0 ? TST/4 + 180 : TST/4 - 180;

            double latR  = latDeg * Math.PI / 180;
            double declR = decl   * Math.PI / 180;
            double HAr   = HA     * Math.PI / 180;

            double cosZ = Math.Sin(latR)*Math.Sin(declR) + Math.Cos(latR)*Math.Cos(declR)*Math.Cos(HAr);
            cosZ = Math.Max(-1, Math.Min(1, cosZ));
            double el   = 90 - Math.Acos(cosZ)*180/Math.PI;

            if (el < 0) return (0, 0);

            double sinZ   = Math.Sin(Math.Acos(cosZ));
            double cosAz  = sinZ > 1e-6
                ? (Math.Sin(latR)*cosZ - Math.Sin(declR)) / (Math.Cos(latR)*sinZ)
                : 0;
            cosAz = Math.Max(-1, Math.Min(1, cosAz));
            double az     = Math.Acos(cosAz) * 180/Math.PI;
            if (HA > 0) az = 360 - az;

            return (az, el);
        }

        // Sun direction vector (North = +Y, Up = +Z)
        public static Vector3d SunDirection(double azDeg, double elDeg)
        {
            double az  = azDeg * Math.PI / 180;
            double el  = elDeg * Math.PI / 180;
            double cosEl = Math.Cos(el);
            return new Vector3d(cosEl*Math.Sin(az), cosEl*Math.Cos(az), Math.Sin(el));
        }

        // ── Shadow scoring ────────────────────────────────────────────────────
        // Returns fraction of mesh faces lit by sun at this angle
        public static double[] ShadowFraction(Mesh mesh, Vector3d sunDir, int samples = 1)
        {
            int fc     = mesh.Faces.Count;
            var result = new double[fc];
            sunDir.Unitize();
            for (int fi = 0; fi < fc; fi++)
            {
                var    f  = mesh.Faces[fi];
                var    nf = mesh.FaceNormals[fi];
                var    n  = new Vector3d(nf.X, nf.Y, nf.Z);
                double dot = Vector3d.Multiply(n, sunDir);
                result[fi] = Math.Max(dot, 0);
            }
            return result;
        }

        // ── Acoustic pressure (matches Bat GH formula) ───────────────────────
        public static double PressureDb(Point3d pt, Point3d src, double level)
        {
            double d = Math.Max(pt.DistanceTo(src), 0.1);
            return level - 20.0 * Math.Log10(d) - 11.0;
        }
    }
}
