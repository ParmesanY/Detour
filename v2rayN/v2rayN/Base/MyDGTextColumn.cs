using System.Windows.Controls;

namespace v2rayN.Base;

/// <summary>Server table column with a stable name used for sorting and saved widths/visibility.</summary>
internal interface IExNameColumn
{
    string ExName { get; }
}

internal class MyDGTextColumn : DataGridTextColumn, IExNameColumn
{
    public string ExName { get; set; }
}

internal class MyDGTemplateColumn : DataGridTemplateColumn, IExNameColumn
{
    public string ExName { get; set; }
}
