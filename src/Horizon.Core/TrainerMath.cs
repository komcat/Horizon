namespace Horizon.Core;

public static class TrainerMath
{
    public static (double originX, double originY, double xAxisX, double xAxisY, double yAxisX, double yAxisY)
        ComputeCoordinateAxes(ReferencePoint ref1, ReferencePoint ref2)
    {
        double dx = ref2.GlobalX - ref1.GlobalX;
        double dy = ref2.GlobalY - ref1.GlobalY;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-12)
            return (ref1.GlobalX, ref1.GlobalY, 1, 0, 0, 1);

        double xAxisX = dx / len;
        double xAxisY = dy / len;
        double yAxisX = -xAxisY;
        double yAxisY = xAxisX;

        return (ref1.GlobalX, ref1.GlobalY, xAxisX, xAxisY, yAxisX, yAxisY);
    }

    public static (double localX, double localY) WorldToLocal(
        double worldX, double worldY,
        double originX, double originY,
        double xAxisX, double xAxisY,
        double yAxisX, double yAxisY)
    {
        double dx = worldX - originX;
        double dy = worldY - originY;
        double localX = dx * xAxisX + dy * xAxisY;
        double localY = dx * yAxisX + dy * yAxisY;
        return (localX, localY);
    }

    public static (double worldX, double worldY) LocalToWorld(
        double localX, double localY,
        double originX, double originY,
        double xAxisX, double xAxisY,
        double yAxisX, double yAxisY)
    {
        double worldX = originX + localX * xAxisX + localY * yAxisX;
        double worldY = originY + localX * xAxisY + localY * yAxisY;
        return (worldX, worldY);
    }

    public static double ComputeScale(
        ReferencePoint taughtRef1, ReferencePoint taughtRef2,
        ReferencePoint actualRef1, ReferencePoint actualRef2)
    {
        double taughtDist = Distance(taughtRef1.GlobalX, taughtRef1.GlobalY,
                                     taughtRef2.GlobalX, taughtRef2.GlobalY);
        double actualDist = Distance(actualRef1.GlobalX, actualRef1.GlobalY,
                                     actualRef2.GlobalX, actualRef2.GlobalY);
        if (taughtDist < 1e-12)
            return 1.0;
        return actualDist / taughtDist;
    }

    public static (double correctedX, double correctedY, double correctedZ)
        ComputeCorrectedPosition(
            BondingPoint bond,
            double liveOriginX, double liveOriginY,
            double liveXAxisX, double liveXAxisY,
            double liveYAxisX, double liveYAxisY,
            double scale)
    {
        double scaledLocalX = bond.LocalX * scale;
        double scaledLocalY = bond.LocalY * scale;

        var (worldX, worldY) = LocalToWorld(
            scaledLocalX, scaledLocalY,
            liveOriginX, liveOriginY,
            liveXAxisX, liveXAxisY,
            liveYAxisX, liveYAxisY);

        return (worldX, worldY, bond.GlobalZ);
    }

    public static (double centerX, double centerY, double centerZ) ComputeCenter(
        List<BondingCorner> corners)
    {
        if (corners.Count == 0)
            return (0, 0, 0);

        double sumX = 0, sumY = 0;
        foreach (var c in corners)
        {
            sumX += c.X;
            sumY += c.Y;
        }

        return (sumX / corners.Count, sumY / corners.Count, corners[0].Z);
    }

    public static (double centerX, double centerY, double centerZ) ComputeRefCenter(
        List<ReferenceCorner> corners)
    {
        if (corners.Count == 0)
            return (0, 0, 0);

        double sumX = 0, sumY = 0;
        foreach (var c in corners)
        {
            sumX += c.X;
            sumY += c.Y;
        }

        return (sumX / corners.Count, sumY / corners.Count, corners[0].Z);
    }

    private static double Distance(double x1, double y1, double x2, double y2)
    {
        double dx = x2 - x1;
        double dy = y2 - y1;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
