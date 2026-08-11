using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Compendium.Ux.Models;
using RetroDownfall.Compendium.Ux.ViewModels;
using Xunit;

namespace RetroDownfall.Compendium.Tests.Compendium;

public sealed class GenericSettingFieldViewModelTests
{
    [Fact]
    public void ValueChange_NotifiesNumericValueOnlyForNumericKinds()
    {
        GenericSettingFieldViewModel numeric = new(
            Descriptor("cost.pricing.defaultPricing.reasoningPer1M", SettingKind.Float),
            1m);
        List<string?> numericNotifications = [];
        numeric.PropertyChanged += (_, args) => numericNotifications.Add(args.PropertyName);

        numeric.Value = 2m;

        Assert.Contains(nameof(GenericSettingFieldViewModel.NumericValue), numericNotifications);
        Assert.DoesNotContain(nameof(GenericSettingFieldViewModel.IsSet), numericNotifications);

        GenericSettingFieldViewModel text = new(
            Descriptor("providers.defaultModel", SettingKind.String),
            "first");
        List<string?> textNotifications = [];
        text.PropertyChanged += (_, args) => textNotifications.Add(args.PropertyName);

        text.Value = "second";

        Assert.DoesNotContain(nameof(GenericSettingFieldViewModel.NumericValue), textNotifications);
        Assert.DoesNotContain(nameof(GenericSettingFieldViewModel.IsSet), textNotifications);
    }

    [Fact]
    public void ValueChange_NotifiesIsSetOnlyWhenNullnessChanges()
    {
        GenericSettingFieldViewModel field = new(
            Descriptor("cost.pricing.defaultPricing.reasoningPer1M", SettingKind.Float),
            value: null);
        List<string?> notifications = [];
        field.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        field.Value = 0m;

        Assert.Contains(nameof(GenericSettingFieldViewModel.NumericValue), notifications);
        Assert.Contains(nameof(GenericSettingFieldViewModel.IsSet), notifications);

        notifications.Clear();
        field.Value = 1m;

        Assert.Contains(nameof(GenericSettingFieldViewModel.NumericValue), notifications);
        Assert.DoesNotContain(nameof(GenericSettingFieldViewModel.IsSet), notifications);

        notifications.Clear();
        field.Value = null;

        Assert.Contains(nameof(GenericSettingFieldViewModel.NumericValue), notifications);
        Assert.Contains(nameof(GenericSettingFieldViewModel.IsSet), notifications);
    }

    [Theory]
    [InlineData(100_000.55)]
    [InlineData(123_456.78)]
    [InlineData(250_000.25)]
    [InlineData(500_000.99)]
    public void Stepper_edits_to_money_settings_keep_their_decimal_precision(double typed)
    {
        SettingDescriptor descriptor = Assert.IsType<SettingDescriptor>(
            SettingDescriptors.Find("cost.budget.dailyLimitUsd"));

        Assert.Equal(SettingKind.Float, descriptor.Kind);

        GenericSettingFieldViewModel field = new(descriptor, 0m);

        field.NumericValue = typed;

        Assert.False(field.HasError);

        ArcanumSettings applied = GenericSettingsUpdater.ApplyFields(new ArcanumSettings(), [field]);

        Assert.Equal((decimal)typed, applied.Cost.Budget.DailyLimitUsd);
    }

    private static SettingDescriptor Descriptor(string key, SettingKind kind) =>
        new(
            key,
            ConfigSection.Cost,
            "Test",
            "Test setting.",
            kind,
            AllowUnset: true);
}
