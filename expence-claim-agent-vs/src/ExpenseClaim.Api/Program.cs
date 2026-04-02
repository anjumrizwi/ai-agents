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
