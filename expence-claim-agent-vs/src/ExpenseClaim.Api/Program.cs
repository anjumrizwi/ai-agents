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

var policyFilePath = Path.Combine(app.Environment.ContentRootPath, "expense-policy.txt");
var expensePolicyText = File.Exists(policyFilePath)
    ? File.ReadAllText(policyFilePath)
    : string.Empty;

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
    var endpoint = Environment.GetEnvironmentVariable("AZURE_FOUNDRY_PROJECT_ENDPOINT")
        ?? builder.Configuration["AzureAiFoundry:ProjectEndpoint"];
    var deploymentName = Environment.GetEnvironmentVariable("AZURE_FOUNDRY_PROJECT_DEPLOYMENT_NAME")
        ?? builder.Configuration["AzureAiFoundry:DeploymentName"];

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

        if (LooksLikePolicyQuestion(request.Text))
        {
            if (string.IsNullOrWhiteSpace(expensePolicyText))
            {
                return Results.BadRequest(new
                {
                    error = "Expense policy knowledge file is missing.",
                    required = "expense-policy.txt"
                });
            }

            #pragma warning disable CS0618
            agent = await projectClient.CreateAIAgentAsync(
                name: "ExpensePolicyAssistant",
                model: deploymentName,
                instructions: "You are an expense policy assistant. Answer only using the policy text below. If answer is not in policy, say that clearly.\n\nExpense Policy:\n" + expensePolicyText);
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

        ExpenseDraft? draft;
        try
        {
            draft = JsonSerializer.Deserialize<ExpenseDraft>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
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

        if (TryGetPolicyLimit(expensePolicyText, draft, out var limit, out var policyCategory) && draft.Amount > limit)
        {
            return Results.BadRequest(new
            {
                error = $"{policyCategory} expense exceeds policy limit.",
                policy = GetPolicyLineForCategory(expensePolicyText, policyCategory),
                limit
            });
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

static bool IsMealCategory(string category)
{
    var normalized = category.Trim().ToLowerInvariant();
    return normalized is "meal" or "meals" or "breakfast" or "lunch" or "dinner";
}

static bool TryGetPolicyLimit(string policyText, ExpenseDraft draft, out decimal limit, out string policyCategory)
{
    limit = 0m;
    policyCategory = string.Empty;

    var normalizedCategory = draft.Category.Trim().ToLowerInvariant();
    var normalizedDescription = draft.Description.Trim().ToLowerInvariant();

    if (IsMatch(normalizedCategory, normalizedDescription, "meals", ["meal", "meals", "breakfast", "lunch", "dinner"]) && TryExtractPolicyAmount(policyText, "Meals", out limit))
    {
        policyCategory = "Meals";
        return true;
    }

    if (IsMatch(normalizedCategory, normalizedDescription, "flights", ["flight", "flights", "airfare"]) && TryExtractPolicyAmount(policyText, "Flights", out limit))
    {
        policyCategory = "Flights";
        return true;
    }

    if (IsMatch(normalizedCategory, normalizedDescription, "taxis and rideshares", ["taxi", "rideshare", "uber", "lyft", "cab"]) && TryExtractPolicyAmount(policyText, "Taxis and Rideshares", out limit))
    {
        policyCategory = "Taxis and Rideshares";
        return true;
    }

    if (IsMatch(normalizedCategory, normalizedDescription, "hotels", ["hotel", "lodging", "accommodation"]) && TryExtractPolicyAmount(policyText, "Hotels", out limit))
    {
        policyCategory = "Hotels";
        return true;
    }

    if (IsMatch(normalizedCategory, normalizedDescription, "other expenses", ["other", "misc", "miscellaneous"]) && TryExtractPolicyAmount(policyText, "Other expenses", out limit))
    {
        policyCategory = "Other expenses";
        return true;
    }

    return false;
}

static bool IsMatch(string category, string description, string categoryKeyword, string[] aliases)
{
    return category.Contains(categoryKeyword)
        || aliases.Any(a => category.Contains(a) || description.Contains(a));
}

static bool TryExtractPolicyAmount(string policyText, string categoryName, out decimal amount)
{
    amount = 0m;
    if (string.IsNullOrWhiteSpace(policyText))
    {
        return false;
    }

    var line = policyText
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .FirstOrDefault(l => l.Contains(categoryName, StringComparison.OrdinalIgnoreCase) && l.Contains('$'));

    if (line is null)
    {
        return false;
    }

    var dollarIndex = line.IndexOf('$');
    if (dollarIndex < 0)
    {
        return false;
    }

    var valueChars = new string(line[(dollarIndex + 1)..].TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
    return decimal.TryParse(valueChars, out amount);
}

static string GetPolicyLineForCategory(string policyText, string categoryName)
{
    return policyText
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .FirstOrDefault(l => l.Contains(categoryName, StringComparison.OrdinalIgnoreCase))
        ?? "Policy limit configured.";
}

record Expense(int Id, string Description, decimal Amount, DateOnly ExpenseDate, string Category);

record CreateExpenseRequest(string Description, decimal Amount, DateOnly ExpenseDate, string Category);

record UpdateExpenseRequest(string Description, decimal Amount, DateOnly ExpenseDate, string Category);

record CreateExpenseFromTextRequest(string Text);

sealed record ExpenseDraft(string Description, decimal Amount, string ExpenseDate, string Category);

public partial class Program;
