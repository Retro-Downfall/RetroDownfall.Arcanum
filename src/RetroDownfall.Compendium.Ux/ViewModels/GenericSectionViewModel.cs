using System.Collections.ObjectModel;
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

            field.PropertyChanged += (_, _) => Root.MarkDirty();

        }

        Fields = newFields;

    }

}
