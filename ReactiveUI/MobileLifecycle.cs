﻿using System;
using System.Reactive;
using System.Reactive.Linq;
using Splat;
using System.Reactive.Disposables;
using System.Reactive.Subjects;

namespace ReactiveUI.Mobile
{
    internal class SuspensionHost : ReactiveObject, ISuspensionHost
    {
        readonly ReplaySubject<IObservable<Unit>> isLaunchingNew = new ReplaySubject<IObservable<Unit>>(1);
        public IObservable<Unit> IsLaunchingNew {
            get { return isLaunchingNew.Switch(); }
            set { isLaunchingNew.OnNext(value); }
        }

        readonly ReplaySubject<IObservable<Unit>> isResuming = new ReplaySubject<IObservable<Unit>>(1);
        public IObservable<Unit> IsResuming {
            get { return isResuming.Switch(); }
            set { isResuming.OnNext(value); }
        }

        readonly ReplaySubject<IObservable<Unit>> isUnpausing = new ReplaySubject<IObservable<Unit>>(1);
        public IObservable<Unit> IsUnpausing {
            get { return isUnpausing.Switch(); }
            set { isUnpausing.OnNext(value); }
        }

        readonly ReplaySubject<IObservable<IDisposable>> shouldPersistState = new ReplaySubject<IObservable<IDisposable>>(1);
        public IObservable<IDisposable> ShouldPersistState {
            get { return shouldPersistState.Switch(); }
            set { shouldPersistState.OnNext(value); }
        }

        readonly ReplaySubject<IObservable<Unit>> shouldInvalidateState = new ReplaySubject<IObservable<Unit>>(1);
        public IObservable<Unit> ShouldInvalidateState {
            get { return shouldInvalidateState.Switch(); }
            set { shouldInvalidateState.OnNext(value); }
        }

        public Func<object> CreateNewAppState { get; set; }

        object appState;
        public object AppState {
            get { return appState; }
            set { this.RaiseAndSetIfChanged(ref appState, value); }
        }

        public SuspensionHost()
        {
#if COCOA
            var message = "Your AppDelegate class needs to derive from AutoSuspendAppDelegate";
#elif ANDROID
            var message = "Your Activities need to instantiate AutoSuspendActivityHelper";
#else
            var message = "Your App class needs to derive from AutoSuspendApplication";
#endif

            IsLaunchingNew = IsResuming = IsUnpausing = ShouldInvalidateState =
                Observable.Throw<Unit>(new Exception(message));

            ShouldPersistState = Observable.Throw<IDisposable>(new Exception(message));
        }
    }

    public static class SuspensionHostExtensions
    {
        public static IObservable<T> ObserveAppState<T>(this ISuspensionHost This)
        {
            return This.WhenAny(x => x.AppState, x => (T)x.Value)
                .Where(x => x != null);
        }

        public static T GetAppState<T>(this ISuspensionHost This)
        {
            return (T)This.AppState;
        }
                
        public static IDisposable SetupDefaultSuspendResume(this ISuspensionHost This, ISuspensionDriver driver = null)
        {
            var ret = new CompositeDisposable();
            driver = driver ?? Locator.Current.GetService<ISuspensionDriver>();

            ret.Add(This.ShouldInvalidateState
                .SelectMany(_ => driver.InvalidateState())
                .LoggedCatch(This, Observable.Return(Unit.Default), "Tried to invalidate app state")
                .Subscribe(_ => This.Log().Info("Invalidated app state")));

            ret.Add(This.ShouldPersistState
                .SelectMany(x => driver.SaveState(This.AppState).Finally(x.Dispose))
                .LoggedCatch(This, Observable.Return(Unit.Default), "Tried to persist app state")
                .Subscribe(_ => This.Log().Info("Persisted application state")));

            ret.Add(Observable.Merge(This.IsResuming, This.IsLaunchingNew)
                .SelectMany(x => driver.LoadState())
                .LoggedCatch(This,
                    Observable.Defer(() => Observable.Return(This.CreateNewAppState())),
                    "Failed to restore app state from storage, creating from scratch")
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(x => This.AppState = x ?? This.CreateNewAppState()));

            return ret;
        }
    }

    public class DummySuspensionDriver : ISuspensionDriver
    {
        public IObservable<object> LoadState()
        {
            return Observable.Return(default(object));
        }

        public IObservable<Unit> SaveState(object state)
        {
            return Observable.Return(Unit.Default);
        }

        public IObservable<Unit> InvalidateState()
        {
            return Observable.Return(Unit.Default);
        }
    }
}
