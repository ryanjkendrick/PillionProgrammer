using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Contoso.Geometry.Tests;

public readonly record struct Point(double X, double Y)
{
    public static Point operator +(Point a, Point b) => new(a.X + b.X, a.Y + b.Y);

    public static Point operator -(Point a, Point b) => new(a.X - b.X, a.Y - b.Y);

    public double Length => Math.Sqrt((X * X) + (Y * Y));
}

public static class Polygon
{
    public static double Area(IReadOnlyList<Point> vertices)
    {
        if (vertices.Count < 3)
        {
            return 0;
        }

        double sum = 0;
        for (var i = 0; i < vertices.Count; i++)
        {
            var a = vertices[i];
            var b = vertices[(i + 1) % vertices.Count];
            sum += (a.X * b.Y) - (b.X * a.Y);
        }

        return Math.Abs(sum) / 2;
    }

    public static double Perimeter(IReadOnlyList<Point> vertices) =>
        vertices.Select((p, i) => (vertices[(i + 1) % vertices.Count] - p).Length).Sum();

    public static Point Centroid(IReadOnlyList<Point> vertices) =>
        new(vertices.Average(p => p.X), vertices.Average(p => p.Y));
}

public sealed class PolygonTests
{
    private static readonly Point[] UnitSquare = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];

    [Fact]
    public void Area_OfTheUnitSquare_IsOne() => Assert.Equal(1.0, Polygon.Area(UnitSquare), precision: 10);

    [Fact]
    public void Perimeter_OfTheUnitSquare_IsFour() => Assert.Equal(4.0, Polygon.Perimeter(UnitSquare), precision: 10);

    [Fact]
    public void Area_IgnoresWindingOrder()
    {
        var clockwise = UnitSquare.Reverse().ToArray();

        Assert.Equal(Polygon.Area(UnitSquare), Polygon.Area(clockwise), precision: 10);
    }

    [Theory]
    [InlineData(3, 4, 5)]
    [InlineData(5, 12, 13)]
    [InlineData(8, 15, 17)]
    [InlineData(7, 24, 25)]
    [InlineData(20, 21, 29)]
    public void Length_OfAPythagoreanTriple_IsTheHypotenuse(double x, double y, double expected) =>
        Assert.Equal(expected, new Point(x, y).Length, precision: 10);

    [Theory]
    [InlineData(new[] { 0.0, 0.0, 4.0, 0.0, 0.0, 3.0 }, 6.0)]
    [InlineData(new[] { -1.5, -1.5, 1.5, -1.5, 1.5, 1.5, -1.5, 1.5 }, 9.0)]
    [InlineData(new[] { 0.0, 0.0, 10.0, 0.0, 10.0, 10.0, 5.0, 15.0, 0.0, 10.0 }, 125.0)]
    public void Area_MatchesKnownShapes(double[] coordinates, double expected)
    {
        var vertices = Enumerable.Range(0, coordinates.Length / 2)
            .Select(i => new Point(coordinates[2 * i], coordinates[(2 * i) + 1]))
            .ToList();

        Assert.Equal(expected, Polygon.Area(vertices), precision: 10);
    }

    [Fact]
    public void Centroid_OfTheUnitSquare_IsItsMiddle()
    {
        var centroid = Polygon.Centroid(UnitSquare);

        Assert.Equal(0.5, centroid.X, precision: 10);
        Assert.Equal(0.5, centroid.Y, precision: 10);
    }

    [Fact]
    public void Area_OfFewerThanThreePoints_IsZero()
    {
        Assert.Equal(0, Polygon.Area([]));
        Assert.Equal(0, Polygon.Area([new Point(1, 2)]));
        Assert.Equal(0, Polygon.Area([new Point(1, 2), new Point(3, 4)]));
    }

    [Fact]
    public void Area_OfARegularHexagon_MatchesTheFormula()
    {
        const double side = 2.0;
        var hexagon = Enumerable.Range(0, 6)
            .Select(k => new Point(side * Math.Cos(Math.PI * k / 3), side * Math.Sin(Math.PI * k / 3)))
            .ToList();

        var expected = 3 * Math.Sqrt(3) / 2 * side * side;

        Assert.Equal(expected, Polygon.Area(hexagon), precision: 9);
    }
}
