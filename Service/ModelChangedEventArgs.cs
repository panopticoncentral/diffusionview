using System;

namespace DiffusionView.Service;

public class ModelChangedEventArgs(string name) : EventArgs
{
    public string Name { get; } = name;
}