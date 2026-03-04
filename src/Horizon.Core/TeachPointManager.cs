using Horizon.Motion;
using Horizon.Motion.Models;

namespace Horizon.Core;

public sealed class TeachPointManager
{
    private readonly TeachPointStore _store = new();
    private readonly UnifiedMotionManager _motionManager;

    private readonly Dictionary<string, TeachPointProduct> _products = new(StringComparer.OrdinalIgnoreCase);
    private string _activeProductName = string.Empty;

    public string ActiveProductName => _activeProductName;

    public event Action? ActiveProductChanged;

    public TeachPointManager(UnifiedMotionManager motionManager)
    {
        _motionManager = motionManager;
    }

    public void Load()
    {
        _products.Clear();
        foreach (var name in _store.GetProductNames())
        {
            var product = _store.LoadProduct(name);
            if (product != null)
                _products[name] = product;
        }

        _activeProductName = _store.GetActiveProductName();
    }

    public List<string> GetProductNames()
    {
        return _products.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public TeachPointProduct? GetProduct(string productName)
    {
        return _products.GetValueOrDefault(productName);
    }

    public void CreateProduct(string productName)
    {
        if (_products.ContainsKey(productName)) return;

        var product = new TeachPointProduct { ProductName = productName };
        _products[productName] = product;
        _store.SaveProduct(product);
    }

    public void RenameProduct(string oldName, string newName)
    {
        if (!_products.TryGetValue(oldName, out var product)) return;
        if (_products.ContainsKey(newName)) return;

        _products.Remove(oldName);
        var renamed = product with { ProductName = newName };
        _products[newName] = renamed;
        _store.RenameProduct(oldName, newName);

        if (_activeProductName.Equals(oldName, StringComparison.OrdinalIgnoreCase))
        {
            _activeProductName = newName;
            _store.SetActiveProductName(newName);
            ApplyActiveProduct();
        }
    }

    public void DeleteProduct(string productName)
    {
        _products.Remove(productName);
        _store.DeleteProduct(productName);

        if (_activeProductName.Equals(productName, StringComparison.OrdinalIgnoreCase))
        {
            _activeProductName = string.Empty;
            _store.SetActiveProductName(string.Empty);
            _motionManager.ClearTeachPositions();
            ActiveProductChanged?.Invoke();
        }
    }

    public void SetActiveProduct(string productName)
    {
        _activeProductName = productName;
        _store.SetActiveProductName(productName);
        ApplyActiveProduct();
        ActiveProductChanged?.Invoke();
    }

    public void ApplyActiveProduct()
    {
        _motionManager.ClearTeachPositions();

        if (string.IsNullOrEmpty(_activeProductName)) return;
        if (!_products.TryGetValue(_activeProductName, out var product)) return;

        foreach (var point in product.Points)
        {
            var pos = new PositionStruct
            {
                X = point.X, Y = point.Y, Z = point.Z,
                U = point.U, V = point.V, W = point.W
            };
            _motionManager.SetTeachPosition(point.DeviceName, point.PositionName, pos);
        }
    }

    public void TeachCurrentPosition(string productName, string deviceName, string positionName)
    {
        if (!_products.TryGetValue(productName, out var product)) return;

        var currentPos = _motionManager.GetCurrentPosition(deviceName);
        if (currentPos == null) return;

        var point = new TeachPoint
        {
            DeviceName = deviceName,
            PositionName = positionName,
            X = currentPos.X, Y = currentPos.Y, Z = currentPos.Z,
            U = currentPos.U, V = currentPos.V, W = currentPos.W
        };

        // Replace existing or add new
        var points = product.Points.ToList();
        var existingIdx = points.FindIndex(p =>
            p.DeviceName.Equals(deviceName, StringComparison.OrdinalIgnoreCase) &&
            p.PositionName.Equals(positionName, StringComparison.OrdinalIgnoreCase));

        if (existingIdx >= 0)
            points[existingIdx] = point;
        else
            points.Add(point);

        var updated = product with { Points = points };
        _products[productName] = updated;
        _store.SaveProduct(updated);

        // If this is the active product, refresh motion overlays
        if (_activeProductName.Equals(productName, StringComparison.OrdinalIgnoreCase))
            ApplyActiveProduct();
    }

    public void UpdatePoint(string productName, string deviceName, string positionName)
    {
        TeachCurrentPosition(productName, deviceName, positionName);
    }

    public void DeletePoint(string productName, string deviceName, string positionName)
    {
        if (!_products.TryGetValue(productName, out var product)) return;

        var points = product.Points
            .Where(p => !(p.DeviceName.Equals(deviceName, StringComparison.OrdinalIgnoreCase) &&
                          p.PositionName.Equals(positionName, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var updated = product with { Points = points };
        _products[productName] = updated;
        _store.SaveProduct(updated);

        if (_activeProductName.Equals(productName, StringComparison.OrdinalIgnoreCase))
            ApplyActiveProduct();
    }

    public void CreatePointManual(string productName, string deviceName, string positionName,
        double x, double y, double z, double u = 0, double v = 0, double w = 0)
    {
        if (!_products.TryGetValue(productName, out var product)) return;

        var point = new TeachPoint
        {
            DeviceName = deviceName,
            PositionName = positionName,
            X = x, Y = y, Z = z,
            U = u, V = v, W = w
        };

        var points = product.Points.ToList();
        var existingIdx = points.FindIndex(p =>
            p.DeviceName.Equals(deviceName, StringComparison.OrdinalIgnoreCase) &&
            p.PositionName.Equals(positionName, StringComparison.OrdinalIgnoreCase));

        if (existingIdx >= 0)
            points[existingIdx] = point;
        else
            points.Add(point);

        var updated = product with { Points = points };
        _products[productName] = updated;
        _store.SaveProduct(updated);

        if (_activeProductName.Equals(productName, StringComparison.OrdinalIgnoreCase))
            ApplyActiveProduct();
    }

    public void UpdatePointCoordinates(string productName, string deviceName, string positionName,
        double x, double y, double z, double u, double v, double w)
    {
        CreatePointManual(productName, deviceName, positionName, x, y, z, u, v, w);
    }

    public bool MoveToPoint(string deviceName, string positionName)
    {
        return _motionManager.MoveToNamedPosition(deviceName, positionName, blocking: false);
    }

    public bool MoveToPosition(string deviceName, double x, double y, double z, double u, double v, double w)
    {
        var pos = new PositionStruct { X = x, Y = y, Z = z, U = u, V = v, W = w };
        return _motionManager.MoveToAbs6(deviceName, pos, blocking: false);
    }
}
