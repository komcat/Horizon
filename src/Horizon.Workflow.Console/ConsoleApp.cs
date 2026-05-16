using Horizon.Common;
using Horizon.Workflow;
using Spectre.Console;

namespace Horizon.WorkflowConsole;

/// <summary>Spectre.Console menu loop over <see cref="IProcessService"/>.</summary>
internal sealed class ConsoleApp
{
    private const string ShowProducts = "Show products";
    private const string CreateProduct = "Create product";
    private const string LoadProduct = "Load product";
    private const string UnloadProduct = "Unload product";
    private const string DeleteProduct = "Delete product";
    private const string Exit = "Exit";
    private const string Back = "Back";

    private readonly IProcessService _service;

    public ConsoleApp(IProcessService service) => _service = service;

    public void Run()
    {
        while (true)
        {
            var active = _service.ActiveProduct ?? "(none)";
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title($"\n[aqua]Workflow[/] — active product: [aqua]{Markup.Escape(active)}[/]")
                    .AddChoices(ShowProducts, CreateProduct, LoadProduct, UnloadProduct, DeleteProduct, Exit));

            switch (choice)
            {
                case ShowProducts:
                    ShowProductList();
                    break;

                case CreateProduct:
                    var name = AnsiConsole.Prompt(new TextPrompt<string>("Product name:"));
                    Report(_service.CreateProduct(name), $"Create product '{name}'");
                    break;

                case LoadProduct:
                    PickProduct("Select product to load:", p =>
                        Report(_service.LoadProduct(p), $"Load product '{p}'"));
                    break;

                case UnloadProduct:
                    _service.UnloadProduct();
                    break;

                case DeleteProduct:
                    PickProduct("Select product to delete:", p =>
                    {
                        if (AnsiConsole.Confirm($"Delete product '{Markup.Escape(p)}'?", defaultValue: false))
                            Report(_service.DeleteProduct(p), $"Delete product '{p}'");
                    });
                    break;

                case Exit:
                    return;
            }
        }
    }

    private void ShowProductList()
    {
        var products = _service.Products;
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Product");
        table.AddColumn("Active");

        if (products.Count == 0)
        {
            table.AddRow("[grey]no products[/]", "[grey]—[/]");
        }
        else
        {
            foreach (var p in products)
            {
                var isActive = string.Equals(p, _service.ActiveProduct, StringComparison.OrdinalIgnoreCase);
                table.AddRow(Markup.Escape(p), isActive ? "[green]yes[/]" : "[grey]—[/]");
            }
        }

        AnsiConsole.Write(table);
    }

    private void PickProduct(string title, Action<string> action)
    {
        var products = _service.Products;
        if (products.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No products.[/]");
            return;
        }

        var pick = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title(title)
                .AddChoices(products.Append(Back)));

        if (pick == Back) return;
        action(pick);
    }

    private static void Report(Result result, string action)
    {
        if (result.IsSuccess)
            AnsiConsole.MarkupLineInterpolated($"[green]OK[/] — {action}");
        else
            AnsiConsole.MarkupLineInterpolated($"[red]FAILED[/] — {action}: {result.Message}");
    }
}
