using System;
using UnityEngine;

namespace com.github.lhervier.ksp.util.mcpserver
{
    /// <summary>Directions and distances on the surface around a vessel, in its local north/east frame.</summary>
    internal static class Geo
    {
        /// <summary>The local north and east unit vectors at a vessel, in world space.</summary>
        public static void NorthEast(Vessel v, out Vector3d north, out Vector3d east)
        {
            // Taken from two nearby points of the surface rather than from the body's axes: no handedness to
            // get right, and the same frame latitude and longitude are given in.
            const double step = 1e-4;
            CelestialBody body = v.mainBody;
            Vector3d here = body.GetWorldSurfacePosition(v.latitude, v.longitude, v.altitude);
            north = (body.GetWorldSurfacePosition(v.latitude + step, v.longitude, v.altitude) - here).normalized;
            east = (body.GetWorldSurfacePosition(v.latitude, v.longitude + step, v.altitude) - here).normalized;
        }

        /// <summary>The local frame at a point of a body, as a rotation: its forward is the local north, its up the
        /// local vertical.</summary>
        public static Quaternion LocalFrame(CelestialBody body, double latitude, double longitude, double altitude)
        {
            const double step = 1e-4;
            Vector3d here = body.GetWorldSurfacePosition(latitude, longitude, altitude);
            Vector3d up = (here - body.position).normalized;
            Vector3d north = Vector3d.Exclude(up, body.GetWorldSurfacePosition(latitude + step, longitude, altitude) - here);
            return Quaternion.LookRotation(north.normalized, up);
        }

        /// <summary>The direction the vessel's control point faces, in degrees from north towards east.</summary>
        public static double Heading(Vessel v)
        {
            return Heading(v, v.ReferenceTransform.up);
        }

        /// <summary>
        /// The direction a positive wheel throttle drives the vessel in, in world space: the up axis of the control
        /// point, or its forward axis when the up axis stands upright, as on a part made to face up in the VAB
        /// (ModuleWheelMotor.GetMotorOrientationSign); of the two, the one closer to the ground.
        /// </summary>
        public static Vector3d RoverForward(Vessel v)
        {
            Transform reference = v.ReferenceTransform;
            Vector3d up = (v.CoMD - v.mainBody.position).normalized;
            return Math.Abs(Vector3d.Dot(reference.up, up)) <= Math.Abs(Vector3d.Dot(reference.forward, up))
                ? (Vector3d)reference.up
                : (Vector3d)reference.forward;
        }

        /// <summary>The direction a rover's front faces, in degrees from north towards east.</summary>
        public static double RoverHeading(Vessel v)
        {
            return Heading(v, RoverForward(v));
        }

        /// <summary>The direction a world-space vector points in at a vessel, in degrees from north towards east.</summary>
        public static double Heading(Vessel v, Vector3d forward)
        {
            Vector3d north, east;
            NorthEast(v, out north, out east);
            return Normalize(Math.Atan2(Vector3d.Dot(forward, east), Vector3d.Dot(forward, north)) * 180.0 / Math.PI);
        }

        /// <summary>The angle of the direction the vessel's control point faces above the local horizon, in
        /// degrees, as the navball shows it: positive above, 90 straight up.</summary>
        public static double Pitch(Vessel v)
        {
            Vector3d up = (v.CoMD - v.mainBody.position).normalized;
            double sine = Vector3d.Dot(((Vector3d)v.ReferenceTransform.up).normalized, up);
            return Math.Asin(Math.Max(-1.0, Math.Min(1.0, sine))) * 180.0 / Math.PI;
        }

        /// <summary>
        /// Where a surface point lies from the vessel: <paramref name="northMetres"/> and
        /// <paramref name="eastMetres"/> along the local axes.
        /// </summary>
        public static void Offset(Vessel v, double latitude, double longitude, out double northMetres, out double eastMetres)
        {
            Vector3d north, east;
            NorthEast(v, out north, out east);
            CelestialBody body = v.mainBody;
            Vector3d delta = body.GetWorldSurfacePosition(latitude, longitude, v.altitude)
                - body.GetWorldSurfacePosition(v.latitude, v.longitude, v.altitude);
            northMetres = Vector3d.Dot(delta, north);
            eastMetres = Vector3d.Dot(delta, east);
        }

        /// <summary>An angle brought into [0, 360).</summary>
        public static double Normalize(double degrees)
        {
            degrees %= 360.0;
            return degrees < 0 ? degrees + 360.0 : degrees;
        }

        /// <summary>The signed difference <paramref name="to"/> − <paramref name="from"/>, in (−180, 180].</summary>
        public static double Delta(double from, double to)
        {
            double d = Normalize(to - from);
            return d > 180.0 ? d - 360.0 : d;
        }
    }
}
