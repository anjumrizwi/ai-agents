using System.Text.Json;
using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Agents.AI;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

var expenses = new List<Expense>
{
    new(1, "Taxi", 35.50m, DateOnly.FromDateTime(DateTime.UtcNow.Date), "Travel")
};

app.MapGet("/", () => Results.Ok(new { message = "ExpenseClaim API is running", endpoints = new[] { "/api/expenses" } }));

var expenseRoutes = app.MapGroup("/api/expenses");

expenseRoutes.MapGet("/", () => Results.Ok(expenses));

expenseRoutes.MapGet("/{id:int}", (int id) =>
{
    var expense = expenses.FirstOrDefault(x => x.Id == id);
    return expense is null ? Results.NotFound() : Results.Ok(expense);
});

expenseRoutes.MapPost("/", (CreateExpenseRequest request) =>
{
    var nextId = expenses.Count == 0 ? 1 : expenses.Max(x => x.Id) + 1;
    var expense = new Expense(nextId, request.Description, request.Amount, request.ExpenseDate, request.Category);
    expenses.Add(expense);
    return Results.Created($"/api/expenses/{expense.Id}", expense);
});

expenseRoutes.MapPost("/ai", async (CreateExpenseFromTextRequest request) =>
{
    var endpoint = builder.Configuration["AzureAiFoundry:ProjectEndpoint"]
        ?? Environment.GetEnvironmentVariable("AZURE_FOUNDRY_PROJECT_ENDPOINT");
    var deploymentName = builder.Configuration["AzureAiFoundry:DeploymentName"]
        ?? Environment.GetEnvironmentVariable("AZURE_FOUNDRY_PROJECT_DEPLOYMENT_NAME");

    if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(deploymentName))
    {
        return Results.BadRequest(new
        {
            error = "Missing Azure Foundry configuration.",
            required = new[]
            {
                "AzureAiFoundry:ProjectEndpoint (or AZURE_FOUNDRY_PROJECT_ENDPOINT)",
                "AzureAiFoundry:DeploymentName (or AZURE_FOUNDRY_PROJECT_DEPLOYMENT_NAME)"
            }
        });
    }

    AIAgent? agent = null;

    try
    {
        var projectClient = new AIProjectClient(new Uri(endpoint), new DefaultAzureCredential());

        agent = await projectClient.CreateAIAgentAsync(
            name: "ExpenseClaimExtractor",
            model: deploymentName,
            instructions: "Extract a single expense claim from user text. Return strict JSON only with: description (string), amount (number), expenseDate (yyyy-MM-dd), category (string). No markdown and no extra keys.");

        var aiOutput = await agent.RunAsync(request.Text);
        var json = aiOutput.ToString();

        if (string.IsNullOrWhiteSpace(json))
        {
            return Results.BadRequest(new { error = "AI response was empty." });
        }

        var draft = JsonSerializer.Deserialize<ExpenseDraft>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (draft is null || string.IsNullOrWhiteSpace(draft.Description) || string.IsNullOrWhiteSpace(draft.Category))
        {
            return Results.BadRequest(new { error = "Could not parse expense details from AI response." });
        }

        if (!DateOnly.TryParse(draft.ExpenseDate, out var expenseDate))
        {
            expenseDate = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        }

        var nextId = expenses.Count == 0 ? 1 : expenses.Max(x => x.Id) + 1;
        var expense = new Expense(nextId, draft.Description, draft.Amount, expenseDate, draft.Category);
        expenses.Add(expense);

        return Results.Created($"/api/expenses/{expense.Id}", expense);
    }
    catch (Exception ex)
    {
        return Results.Problem($"AI expense extraction failed: {ex.Message}");
    }
    finally
    {
        if (agent is not null)
        {
            try
            {
                var projectClient = new AIProjectClient(new Uri(endpoint), new DefaultAzureCredential());
                await projectClient.Agents.DeleteAgentAsync(agent.Name);
            }
            catch
            {
            }
        }
    }
});

expenseRoutes.MapPut("/{id:int}", (int id, UpdateExpenseRequest request) =>
{
    var index = expenses.FindIndex(x => x.Id == id);
    if (index < 0)
    {
        return Results.NotFound();
    }

    expenses[index] = new Expense(id, request.Description, request.Amount, request.ExpenseDate, request.Category);
    return Results.NoContent();
});

expenseRoutes.MapDelete("/{id:int}", (int id) =>
{
    var index = expenses.FindIndex(x => x.Id == id);
    if (index < 0)
    {
        return Results.NotFound();
    }

    expenses.RemoveAt(index);
    return Results.NoContent();
});

app.Run();

record Expense(int Id, string Description, decimal Amount, DateOnly ExpenseDate, string Category);

record CreateExpenseRequest(string Description, decimal Amount, DateOnly ExpenseDate, string Category);

record UpdateExpenseRequest(string Description, decimal Amount, DateOnly ExpenseDate, string Category);

record CreateExpenseFromTextRequest(string Text);

sealed record ExpenseDraft(string Description, decimal Amount, string ExpenseDate, string Category);

public partial class Program;
