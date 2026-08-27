namespace TrackEditor.Core.Services;

/// <summary>
/// Position of the sun in the local sky for a moment in time and a place on Earth. Used by the 3D view
/// to light the terrain from the sun's real direction at the track's recorded time.
/// </summary>
/// <remarks>
/// A compact low-precision solar ephemeris (NOAA/Astronomical-Almanac style, good to a fraction of a
/// degree — ample for shading). No atmospheric refraction is applied.
/// </remarks>
public static class SolarPosition
{
    private const double Deg2Rad = Math.PI / 180.0;
    private const double Rad2Deg = 180.0 / Math.PI;

    /// <summary>
    /// Sun altitude (° above the horizon; negative below) and azimuth (° clockwise from true north:
    /// 0 = N, 90 = E, 180 = S, 270 = W) for a UTC instant at the given latitude/longitude.
    /// </summary>
    public static (double AzimuthDeg, double AltitudeDeg) AltAz(DateTime utc, double latDeg, double lonDeg)
    {
        // Days since the J2000.0 epoch (2000-01-01 12:00 UTC).
        double n = utc.ToOADate() + 2415018.5 - 2451545.0;

        double meanLon = Norm360(280.460 + 0.9856474 * n);          // sun mean longitude
        double meanAnom = Norm360(357.528 + 0.9856003 * n) * Deg2Rad; // sun mean anomaly

        // Ecliptic longitude (equation of centre applied) and obliquity of the ecliptic.
        double eclLon = (meanLon + 1.915 * Math.Sin(meanAnom) + 0.020 * Math.Sin(2 * meanAnom)) * Deg2Rad;
        double obliq = (23.439 - 0.0000004 * n) * Deg2Rad;

        // Equatorial coordinates.
        double rightAsc = Math.Atan2(Math.Cos(obliq) * Math.Sin(eclLon), Math.Cos(eclLon)) * Rad2Deg;
        double decl = Math.Asin(Math.Sin(obliq) * Math.Sin(eclLon)); // radians

        // Greenwich then local mean sidereal time → hour angle of the sun.
        double gmst = Norm360(280.46061837 + 360.98564736629 * n);
        double lst = Norm360(gmst + lonDeg);
        double hourAngle = NormPm180(lst - rightAsc) * Deg2Rad;

        double lat = latDeg * Deg2Rad;
        double alt = Math.Asin(Math.Sin(lat) * Math.Sin(decl) +
                               Math.Cos(lat) * Math.Cos(decl) * Math.Cos(hourAngle));

        double cosAz = (Math.Sin(decl) - Math.Sin(lat) * Math.Sin(alt)) / (Math.Cos(lat) * Math.Cos(alt));
        double az = Math.Acos(Math.Clamp(cosAz, -1.0, 1.0)) * Rad2Deg; // 0..180 from north
        if (Math.Sin(hourAngle) > 0) az = 360.0 - az;                  // afternoon → western sky

        return (az, alt * Rad2Deg);
    }

    private static double Norm360(double d) { d %= 360.0; return d < 0 ? d + 360.0 : d; }
    private static double NormPm180(double d) { d = Norm360(d); return d > 180.0 ? d - 360.0 : d; }
}
