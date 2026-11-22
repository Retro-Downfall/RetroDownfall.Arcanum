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
    [InlineData("100000.55")]
    [InlineData("123456.78")]
    [InlineData("500000.99")]
    [InlineData("0.7")]
    [InlineData("123456.7890123456789012345678")]
    [InlineData("0.0000000000000000000000000001")]
    public void Stepper_edits_to_money_settings_keep_their_decimal_precision(string text)
    {
        decimal typed = decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

        SettingDescriptor descriptor = Assert.IsType<SettingDescriptor>(
            SettingDescriptors.Find("cost.pricing.defaultPricing.inputPer1M"));

        GenericSettingFieldViewModel field = new(descriptor, 0m);

        field.NumericValue = typed;

        Assert.False(field.HasError);

        Assert.Equal(typed, Assert.IsType<decimal>(field.Value));

        ArcanumSettings applied = GenericSettingsUpdater.ApplyFields(new ArcanumSettings(), [field]);

        Assert.Equal(typed, applied.Cost.Pricing.DefaultPricing.InputPer1M);
    }

    public static IEnumerable<object[]> UnsupportedBinaryMoney()
    {
        yield return [0.7d];

        yield return [0.7f];

        yield return [double.MaxValue];

        yield return [double.NaN];

        yield return [double.PositiveInfinity];

        yield return [double.NegativeInfinity];
    }

    [Theory]
    [MemberData(nameof(UnsupportedBinaryMoney))]
    public void Boxed_binary_money_is_invalid_and_cannot_replace_existing_decimal(object supplied)
    {
        const decimal originalAmount = 12.34567890123456789m;

        ArcanumSettings original = new();

        original.Cost.Pricing.DefaultPricing.InputPer1M = originalAmount;

        SettingDescriptor descriptor = Assert.IsType<SettingDescriptor>(
            SettingDescriptors.Find("cost.pricing.defaultPricing.inputPer1M"));

        GenericSettingFieldViewModel field = new(descriptor, supplied);

        Assert.True(field.HasError);

        _ = field.NumericValue; // A binding read must remain safe even while validation refuses it.

        ArcanumSettings preserved = GenericSettingsUpdater.ApplyFields(original, [field]);

        Assert.Equal(originalAmount, preserved.Cost.Pricing.DefaultPricing.InputPer1M);

        field.ErrorMessage = null;

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => GenericSettingsUpdater.ApplyFields(original, [field]));

        Assert.Contains(descriptor.Key, failure.Message, StringComparison.Ordinal);

        Assert.Equal(originalAmount, original.Cost.Pricing.DefaultPricing.InputPer1M);
    }

    [Fact]
    public void Nullable_money_also_refuses_binary_values_when_validation_is_cleared()
    {
        const decimal originalAmount = 12.34567890123456789m;

        ArcanumSettings original = new();

        original.Cost.Pricing.DefaultPricing.ReasoningPer1M = originalAmount;

        SettingDescriptor descriptor = Assert.IsType<SettingDescriptor>(
            SettingDescriptors.Find("cost.pricing.defaultPricing.reasoningPer1M"));

        GenericSettingFieldViewModel field = new(descriptor, 0.7d);

        Assert.True(field.HasError);

        Assert.Equal(0m, field.NumericValue);

        Assert.Equal(originalAmount, GenericSettingsUpdater.ApplyFields(original, [field])
            .Cost.Pricing.DefaultPricing.ReasoningPer1M);

        field.ErrorMessage = null;

        Assert.Throws<InvalidOperationException>(() => GenericSettingsUpdater.ApplyFields(original, [field]));

        Assert.Equal(originalAmount, original.Cost.Pricing.DefaultPricing.ReasoningPer1M);
    }

    [Theory]
    [InlineData("1000000.0000000000000000001", false)]
    [InlineData("999999.9999999999999999999", true)]
    public void Money_range_validation_uses_the_exact_decimal(string text, bool valid)
    {
        decimal amount = decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

        SettingDescriptor descriptor = Assert.IsType<SettingDescriptor>(
            SettingDescriptors.Find("cost.pricing.defaultPricing.inputPer1M"));

        GenericSettingFieldViewModel field = new(descriptor, amount);

        Assert.Equal(!valid, field.HasError);

        ArcanumSettings original = new();

        original.Cost.Pricing.DefaultPricing.InputPer1M = 5m;

        Assert.Equal(valid ? amount : 5m,
            GenericSettingsUpdater.ApplyFields(original, [field]).Cost.Pricing.DefaultPricing.InputPer1M);
    }

    [Theory]
    [InlineData(SettingKind.Int, "2.5", "2")]
    [InlineData(SettingKind.Int, "3.5", "4")]
    [InlineData(SettingKind.Long, "9007199254740992.5", "9007199254740992")]
    [InlineData(SettingKind.Long, "9007199254740993.5", "9007199254740994")]
    public void Integer_edits_keep_exact_ties_to_even(SettingKind kind, string text, string expected)
    {
        SettingDescriptor descriptor = new("test.integer", ConfigSection.Cost, "Integer", "Integer", kind,
            Min: 0, Max: long.MaxValue);

        GenericSettingFieldViewModel field = new(descriptor, 0);

        field.NumericValue = decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

        Assert.False(field.HasError);

        if (kind == SettingKind.Int)
        {
            Assert.Equal(int.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), Assert.IsType<int>(field.Value));
        }
        else
        {
            Assert.Equal(long.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), Assert.IsType<long>(field.Value));
        }
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
