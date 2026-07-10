using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RetroDownfall.Compendium.Ux.Models;

namespace RetroDownfall.Compendium.Ux.ViewModels;

public sealed class GenericSectionViewModel : ObservableObject
{

    public GenericSectionViewModel(ConfigurationViewModel root, ConfigSection section)
    {

        Root = root;

        Section = section;

        Fields = new ObservableCollection<GenericSettingFieldViewModel>();

    }

    public ConfigurationViewModel Root { get; }

    public ConfigSection Section { get; }

    public ObservableCollection<GenericSettingFieldViewModel> Fields { get; }

    public void LoadFrom(IEnumerable<GenericSettingFieldViewModel> fields)
    {

        Fields.Clear();

        foreach (GenericSettingFieldViewModel field in fields)
        {

            Fields.Add(field);

            field.PropertyChanged += (_, _) => Root.MarkDirty();

        }

    }

}
