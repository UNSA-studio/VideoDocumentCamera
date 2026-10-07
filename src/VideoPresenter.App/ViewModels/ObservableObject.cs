// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// Required Notice: Copyright (C) 2026 UNSA Studio
//
// 本文件属于视频展台（VideoPresenter）。分发时请随文件一并提供许可条款：
// https://polyformproject.org/licenses/noncommercial/1.0.0
//
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VideoPresenter.App.ViewModels;

/// <summary>轻量 MVVM 基类（不引入第三方框架，保持启动路径最短）。</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

/// <summary>
/// 轻量 ICommand 实现。
/// 注意：WinUI 3 没有 WPF 的 <c>CommandManager.RequerySuggested</c>，
/// 因此提供 <see cref="RaiseCanExecuteChanged"/> 供 ViewModel 主动通知。
/// </summary>
public sealed class RelayCommand : System.Windows.Input.ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = _ => execute();
        _canExecute = canExecute is null ? null : _ => canExecute();
    }

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => _execute(parameter);

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}