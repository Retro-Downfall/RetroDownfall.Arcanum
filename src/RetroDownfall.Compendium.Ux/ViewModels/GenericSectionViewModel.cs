using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RetroDownfall.Compendium.Ux.Models;

namespace RetroDownfall.Compendium.Ux.ViewModels;

public sealed class GenericSectionViewModel : ObservableObject
{
    private ObservableCollection<GenericSettingFieldViewModel> _fields = [];

    public GenericSectionViewModel(ConfigurationViewModel root, ConfigSection section)
    {

        Root = root;

        Section = section;

    }

    public ConfigurationViewModel Root { get; }

    public ConfigSection Section { get; }

    public ObservableCollection<GenericSettingFieldViewModel> Fields
    {
        get => _fields;

        private set => SetProperty(ref _fields, value);
    }

    public void LoadFrom(IEnumerable<GenericSettingFieldViewModel> fields)
    {

        // Batch update: create new collection and replace to minimize UI notifications
        ObservableCollection<GenericSettingFieldViewModel> newFields = new();

        foreach (GenericSettingFieldViewModel field in fields)
        {

            newFields.Add(field);

            field.PropertyChanged += OnFieldPropertyChanged;

        }

        Fields = newFields;

    }

    /// <summary>
    /// Only the value itself and its validation outcome change the editor's dirty or error state. Every
    /// edit also raises the derived projections (StringValue, BoolValue, NumericValue, IsSet, HasError),
    /// and relaying those would run one full validation sweep of every field of every opened section per
    /// projection instead of once per edit.
    /// </summary>
    private void OnFieldPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {

        if (e.PropertyName is not (nameof(GenericSettingFieldViewModel.Value)
            or nameof(GenericSettingFieldViewModel.ErrorMessage)))
        {

            return;

        }

        Root.MarkDirty();

    }

}
