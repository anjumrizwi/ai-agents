using System.Text.Json;
using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Agents.AI;

const string ExpensePolicyFileName = "expense-policy.txt";
const string FoundryEndpointEnvVar = "AZURE_FOUNDRY_PROJECT_ENDPOINT";
const string FoundryDeploymentEnvVar = "AZURE_FOUNDRY_PROJECT_DEPLOYMENT_NAME";
const string FoundryEndpointConfigKey = "AzureAiFoundry:ProjectEndpoint";
const string FoundryDeploymentConfigKey = "AzureAiFoundry:DeploymentName";

var expenseDraftJsonOptions = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true
};

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddSingleton<Func<AIProjectClient?>>(serviceProvider =>
{
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();

    return () =>
    {
        var endpoint = Environment.GetEnvironmentVariable(FoundryEndpointEnvVar)
            ?? configuration[FoundryEndpointConfigKey];

        return string.IsNullOrWhiteSpace(endpoint)
            ? null
            : new AIProjectClient(new Uri(endpoint), new DefaultAzureCredential());
    };
});

var seedExpense = new Expense(1, "Taxi", 35.50m, DateOnly.FromDateTime(DateTime.UtcNow.Date), "Travel");
builder.Services.AddSingleton<IExpenseStore>(_ => new ConcurrentExpenseStore(seedExpense));

var policyFilePath = Path.Combine(builder.Environment.ContentRootPath, ExpensePolicyFileName);
var expensePolicyText = File.Exists(policyFilePath)
    ? File.ReadAllText(policyFilePath)
    : string.Empty;
builder.Services.AddSingleton<IPolicyEngine>(_ => new PolicyEngine(expensePolicyText));

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapGet("/", () => Results.Ok(new { message = "ExpenseClaim API is running", endpoints = new[] { "/api/expenses" } }));

var expenseRoutes = app.MapGroup("/api/expenses");

expenseRoutes.MapGet("/", (IExpenseStore expenseStore) => Results.Ok(expenseStore.GetAll()));

expenseRoutes.MapGet("/{id:int}", (int id, IExpenseStore expenseStore) =>
{
    return expenseStore.TryGet(id, out var expense)
        ? Results.Ok(expense)
        : Results.NotFound();
});

expenseRoutes.MapPost("/", (CreateExpenseRequest request, IExpenseStore expenseStore, IPolicyEngine policyEngine) =>
{
    if (policyEngine.TryGetViolation(request.Description, request.Category, request.Amount, out var violation))
    {
        return Results.BadRequest(new
        {
            error = $"{violation.PolicyCategory} expense exceeds policy limit.",
            policy = violation.PolicyLine,
            limit = violation.Limit
        });
    }

    var expense = expenseStore.Add(request.Description, request.Amount, request.ExpenseDate, request.Category);
    return Results.Created($"/api/expenses/{expense.Id}", expense);
});

expenseRoutes.MapPost("/ai", async (
    CreateExpenseFromTextRequest request,
    Func<AIProjectClient?> projectClientFactory,
    IExpenseStore expenseStore,
    IPolicyEngine policyEngine,
    ILogger<Program> logger) =>
{
    var projectClient = projectClientFactory();
    var deploymentName = Environment.GetEnvironmentVariable(FoundryDeploymentEnvVar)
        ?? builder.Configuration[FoundryDeploymentConfigKey];

    if (projectClient is null || string.IsNullOrWhiteSpace(deploymentName))
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
        if (LooksLikePolicyQuestion(request.Text))
        {
            if (!policyEngine.HasPolicyText)
            {
                return Results.BadRequest(new
                {
                    error = "Expense policy knowledge file is missing.",
                    required = ExpensePolicyFileName
                });
            }

            #pragma warning disable CS0618
            agent = await projectClient.CreateAIAgentAsync(
                name: "ExpensePolicyAssistant",
                model: deploymentName,
                instructions: "You are an expense policy assistant. Answer only using the policy text below. If answer is not in policy, say that clearly.\n\nExpense Policy:\n" + policyEngine.PolicyText);
            #pragma warning restore CS0618

            var policyAnswer = (await agent.RunAsync(request.Text)).ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(policyAnswer))
            {
                return Results.BadRequest(new { error = "AI response was empty." });
            }

            return Results.Ok(new { answer = policyAnswer });
        }

        #pragma warning disable CS0618
        agent = await projectClient.CreateAIAgentAsync(
            name: "ExpenseClaimExtractor",
            model: deploymentName,
            instructions: "Extract a single expense claim from user text. Return strict JSON only with: description (string), amount (number), expenseDate (yyyy-MM-dd), category (string). No markdown and no extra keys.");
        #pragma warning restore CS0618

        var responseText = (await agent.RunAsync(request.Text)).ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return Results.BadRequest(new { error = "AI response was empty." });
        }

        var json = NormalizeJsonPayload(responseText);

        ExpenseDraft? draft;
        try
        {
            draft = JsonSerializer.Deserialize<ExpenseDraft>(json, expenseDraftJsonOptions);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "Could not parse expense details from AI response." });
        }

        if (draft is null || string.IsNullOrWhiteSpace(draft.Description) || string.IsNullOrWhiteSpace(draft.Category))
        {
            return Results.BadRequest(new
            {
                error = "Could not parse expense details from AI response.",
                hint = "Provide a claim statement with amount, date, description, and category."
            });
        }

        if (!DateOnly.TryParse(draft.ExpenseDate, out var expenseDate))
        {
            expenseDate = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        }

        if (policyEngine.TryGetViolation(draft.Description, draft.Category, draft.Amount, out var aiViolation))
        {
            return Results.BadRequest(new
            {
                error = $"{aiViolation.PolicyCategory} expense exceeds policy limit.",
                policy = aiViolation.PolicyLine,
                limit = aiViolation.Limit
            });
        }

        var expense = expenseStore.Add(draft.Description, draft.Amount, expenseDate, draft.Category);
        return Results.Created($"/api/expenses/{expense.Id}", expense);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "AI expense extraction failed.");
        return Results.Problem("AI expense extraction failed.");
    }
    finally
    {
        if (agent is not null)
        {
            try
            {
                await projectClient.Agents.DeleteAgentAsync(agent.Name);
            }
            catch (Exception cleanupEx)
            {
                logger.LogError(cleanupEx, "Failed to cleanup AI agent {AgentName}", agent.Name);
            }
        }
    }
});

expenseRoutes.MapPut("/{id:int}", (int id, UpdateExpenseRequest request, IExpenseStore expenseStore, IPolicyEngine policyEngine) =>
{
    if (policyEngine.TryGetViolation(request.Description, request.Category, request.Amount, out var violation))
    {
        return Results.BadRequest(new
        {
            error = $"{violation.PolicyCategory} expense exceeds policy limit.",
            policy = violation.PolicyLine,
            limit = violation.Limit
        });
    }

    return expenseStore.TryUpdate(id, request.Description, request.Amount, request.ExpenseDate, request.Category)
        ? Results.NoContent()
        : Results.NotFound();
});

expenseRoutes.MapDelete("/{id:int}", (int id, IExpenseStore expenseStore) =>
{
    return expenseStore.TryDelete(id)
        ? Results.NoContent()
        : Results.NotFound();
});

app.Run();

static string NormalizeJsonPayload(string responseText)
{
    var json = responseText;

    if (json.StartsWith("```", StringComparison.Ordinal))
    {
        json = json.Trim('`').Trim();
        if (json.StartsWith("json", StringComparison.OrdinalIgnoreCase))
        {
            json = json[4..].Trim();
        }
    }

    var objectStart = json.IndexOf('{');
    var objectEnd = json.LastIndexOf('}');
    if (objectStart >= 0 && objectEnd > objectStart)
    {
        json = json[objectStart..(objectEnd + 1)];
    }

    return json;
}

static bool LooksLikePolicyQuestion(string text)
{
    if (string.IsNullOrWhiteSpace(text))
    {
        return true;
    }

    var value = text.Trim();
    if (value.EndsWith("?", StringComparison.Ordinal))
    {
        return true;
    }

    var lower = value.ToLowerInvariant();
    return lower.StartsWith("what")
        || lower.StartsWith("how much")
        || lower.StartsWith("can i")
        || lower.StartsWith("is ")
        || lower.StartsWith("are ")
        || lower.Contains("maximum i can claim")
        || lower.Contains("max claim")
        || lower.Contains("policy");
}

record Expense(int Id, string Description, decimal Amount, DateOnly ExpenseDate, string Category);
record CreateExpenseRequest(string Description, decimal Amount, DateOnly ExpenseDate, string Category);
record UpdateExpenseRequest(string Description, decimal Amount, DateOnly ExpenseDate, string Category);
record CreateExpenseFromTextRequest(string Text);
sealed record ExpenseDraft(string Description, decimal Amount, string ExpenseDate, string Category);

public partial class Program;
