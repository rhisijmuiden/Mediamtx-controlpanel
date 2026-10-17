using System.ComponentModel.DataAnnotations;
using MediaMtxPanel.Services.Localization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace MediaMtxPanel.Components.Shared;

/// <summary>
/// Same as &lt;DataAnnotationsValidator /&gt;, but shows the validation messages in the visitor's language:
/// each message (the English ErrorMessage of the attribute) goes through <see cref="TextLocalizer"/>.
/// </summary>
public sealed class LocalizedValidator : ComponentBase, IDisposable
{
    [CascadingParameter] private EditContext? EditContext { get; set; }
    [Inject] private TextLocalizer L { get; set; } = default!;

    private EditContext? subscribed;
    private ValidationMessageStore? messages;

    protected override void OnParametersSet()
    {
        if (EditContext is null)
            throw new InvalidOperationException($"{nameof(LocalizedValidator)} must be placed inside an EditForm.");
        if (ReferenceEquals(EditContext, subscribed))
            return;
        Unsubscribe();
        subscribed = EditContext;
        messages = new ValidationMessageStore(EditContext);
        EditContext.OnValidationRequested += ValidateModel;
        EditContext.OnFieldChanged += ValidateField;
    }

    private void ValidateModel(object? sender, ValidationRequestedEventArgs e)
    {
        var model = subscribed!.Model;
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(model, new ValidationContext(model), results, validateAllProperties: true);
        messages!.Clear();
        foreach (var result in results)
        {
            var members = result.MemberNames.Any() ? result.MemberNames : [string.Empty];
            foreach (var member in members)
                messages.Add(subscribed.Field(member), Translate(result));
        }
        subscribed.NotifyValidationStateChanged();
    }

    private void ValidateField(object? sender, FieldChangedEventArgs e)
    {
        var field = e.FieldIdentifier;
        var property = field.Model.GetType().GetProperty(field.FieldName);
        if (property is null)
            return;
        var results = new List<ValidationResult>();
        Validator.TryValidateProperty(property.GetValue(field.Model), new ValidationContext(field.Model) { MemberName = field.FieldName }, results);
        messages!.Clear(field);
        foreach (var result in results)
            messages.Add(field, Translate(result));
        subscribed!.NotifyValidationStateChanged();
    }

    private string Translate(ValidationResult result) => L[result.ErrorMessage ?? ""];

    private void Unsubscribe()
    {
        if (subscribed is null)
            return;
        subscribed.OnValidationRequested -= ValidateModel;
        subscribed.OnFieldChanged -= ValidateField;
        messages?.Clear();
    }

    public void Dispose() => Unsubscribe();
}
