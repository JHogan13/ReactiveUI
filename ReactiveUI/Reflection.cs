﻿using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Linq.Expressions;
using System.Reactive.Linq;
using System.Reflection;
using ReactiveUI;
using Splat;

namespace ReactiveUI
{
    public static class Reflection 
    {
        static readonly MemoizingMRUCache<Tuple<Type, string>, Func<object, object>> propReaderCache = 
            new MemoizingMRUCache<Tuple<Type, string>, Func<object, object>>((x,_) => {
                var fi = x.Item1.GetRuntimeFields()
                    .FirstOrDefault(y => !y.IsStatic && y.Name == x.Item2);
                if (fi != null) {
                    return (fi.GetValue);
                }

                var pi = x.Item1.GetRuntimeProperties()
                    .FirstOrDefault(y => !y.IsStatic() && y.Name == x.Item2);

                if (pi != null) {
                    return (y => pi.GetValue(y, null));
                }

                return null;
            }, RxApp.BigCacheLimit);

        static readonly MemoizingMRUCache<Tuple<Type, string>, Action<object, object>> propWriterCache = 
            new MemoizingMRUCache<Tuple<Type, string>, Action<object, object>>((x,_) => {
                var fi = x.Item1.GetRuntimeFields()
                    .FirstOrDefault(y => y.IsPublic && !y.IsStatic && y.Name == x.Item2);

                if (fi != null) {
                    return (fi.SetValue);
                }

                var pi =  x.Item1.GetRuntimeProperties()
                    .FirstOrDefault(y => !y.IsStatic() && y.Name == x.Item2);

                if (pi != null) {
                    return ((y,v) => pi.SetValue(y, v, null));
                }

                return null;
            }, RxApp.BigCacheLimit);

        public static string SimpleExpressionToPropertyName<TObj, TRet>(Expression<Func<TObj, TRet>> property)
        {
            Contract.Requires(property != null);

            string propName = null;

            try {
                var propExpr = property.Body as MemberExpression;
                if (propExpr.Expression.NodeType != ExpressionType.Parameter) {
                    throw new ArgumentException("Property expression must be of the form 'x => x.SomeProperty'");
                }

                propName = propExpr.Member.Name;
            } catch (NullReferenceException) {
                throw new ArgumentException("Property expression must be of the form 'x => x.SomeProperty'");
            }

            return propName;
        }

        public static string[] ExpressionToPropertyNames<TObj, TRet>(Expression<Func<TObj, TRet>> property)
        {
            var ret = new List<string>();

            var current = property.Body;
            while(current.NodeType != ExpressionType.Parameter) {

                // This happens when a value type gets boxed
                if (current.NodeType == ExpressionType.Convert || current.NodeType == ExpressionType.ConvertChecked) {
                    var ue = (UnaryExpression) current;
                    current = ue.Operand;
                    continue;
                }

                if (current.NodeType != ExpressionType.MemberAccess) {
                    throw new ArgumentException("Property expression must be of the form 'x => x.SomeProperty.SomeOtherProperty'");
                }

                var me = (MemberExpression)current;
                ret.Insert(0, me.Member.Name);
                current = me.Expression;
            }

            return ret.ToArray();
        }

        public static Type[] ExpressionToPropertyTypes<TObj, TRet>(Expression<Func<TObj, TRet>> property)
        {
            var current = property.Body;

            while(current.NodeType != ExpressionType.Parameter) {
                // This happens when a value type gets boxed
                if (current.NodeType == ExpressionType.Convert || current.NodeType == ExpressionType.ConvertChecked) {
                    var ue = (UnaryExpression) current;
                    current = ue.Operand;
                    continue;
                }

                if (current.NodeType != ExpressionType.MemberAccess) {
                    throw new ArgumentException("Property expression must be of the form 'x => x.SomeProperty.SomeOtherProperty'");
                }

                var me = (MemberExpression)current;
                current = me.Expression;
            }

            var startingType = ((ParameterExpression) current).Type;
            var propNames = ExpressionToPropertyNames(property);

            return GetTypesForPropChain(startingType, propNames);
        }

        public static Type[] GetTypesForPropChain(Type startingType, string[] propNames)
        {
            return propNames.Aggregate(new List<Type>(new[] {startingType}), (acc, x) => {
                var type = acc.Last();

                var pi = type.GetRuntimeProperties().FirstOrDefault(y => y.Name == x);
                if (pi != null) {
                    acc.Add(pi.PropertyType);
                    return acc;
                }

                var fi = type.GetRuntimeFields().FirstOrDefault(y => y.Name == x);
                if (fi != null) {
                    acc.Add(fi.FieldType);
                    return acc;
                }

                throw new ArgumentException("Property expression must be of the form 'x => x.SomeProperty.SomeOtherProperty'");
            }).Skip(1).ToArray();
        }        

        public static Func<TObj, object> GetValueFetcherForProperty<TObj>(string propName)
        {
            var ret = GetValueFetcherForProperty(typeof(TObj), propName);
            return x => (TObj) ret(x);
        }

        public static Func<object, object> GetValueFetcherForProperty(Type type, string propName)
        {
            Contract.Requires(type != null);
            Contract.Requires(propName != null);

            lock (propReaderCache) {
                return propReaderCache.Get(Tuple.Create(type, propName));
            }
        }

        public static Func<object, object> GetValueFetcherOrThrow(Type type, string propName)
        {
            var ret = GetValueFetcherForProperty(type, propName);

            if (ret == null) {
                throw new ArgumentException(String.Format("Type '{0}' must have a property '{1}'", type, propName));
            }
            return ret;
        }

        public static Action<object, object> GetValueSetterForProperty(Type type, string propName)
        {
            Contract.Requires(type != null);
            Contract.Requires(propName != null);

            lock (propReaderCache) {
                return propWriterCache.Get(Tuple.Create(type, propName));
            }
        }

        public static Action<object, object> GetValueSetterOrThrow(Type type, string propName)
        {
            var ret = GetValueSetterForProperty(type, propName);

            if (ret == null) {
                throw new ArgumentException(String.Format("Type '{0}' must have a property '{1}'", type, propName));
            }
            return ret;
        }

        public static bool TryGetValueForPropertyChain<TValue>(out TValue changeValue, object current, string[] propNames)
        {
            foreach (var propName in propNames.SkipLast(1)) {
                if (current == null) {
                    changeValue = default(TValue);
                    return false;
                }

                current = GetValueFetcherOrThrow(current.GetType(), propName)(current);
            }

            if (current == null) {
                changeValue = default(TValue);
                return false;
            }

            changeValue = (TValue) GetValueFetcherOrThrow(current.GetType(), propNames.Last())(current);
            return true;
        }

        public static bool TryGetAllValuesForPropertyChain(out IObservedChange<object, object>[] changeValues, object current, string[] propNames)
        {
            int currentIndex = 0;
            changeValues = new IObservedChange<object,object>[propNames.Length];

            foreach (var propName in propNames.SkipLast(1)) {
                if (current == null) {
                    changeValues[currentIndex] = null;
                    return false;
                }

                var sender = current;
                current = GetValueFetcherOrThrow(current.GetType(), propName)(current);
                var box = new ObservedChange<object, object>(sender, propName, current);

                changeValues[currentIndex] = box;
                currentIndex++;
            }

            if (current == null) {
                changeValues[currentIndex] = null;
                return false;
            }

            changeValues[currentIndex] = new ObservedChange<object, object>(current, propNames.Last(), GetValueFetcherOrThrow(current.GetType(), propNames.Last())(current));

            return true;
        }

        public static bool TrySetValueToPropertyChain<TValue>(object target, string[] propNames, TValue value, bool shouldThrow = true)
        {
            foreach (var propName in propNames.SkipLast(1)) {
                var getter = shouldThrow ?
                    GetValueFetcherOrThrow(target.GetType(), propName) :
                    GetValueFetcherForProperty(target.GetType(), propName);

                target = getter(target);
            }

            if (target == null) return false;

            var setter = shouldThrow ?
                GetValueSetterOrThrow(target.GetType(), propNames.Last()) :
                GetValueSetterForProperty(target.GetType(), propNames.Last());

            if (setter == null) return false;
            setter(target, value);
            return true;
        }

        static readonly MemoizingMRUCache<string, Type> typeCache = new MemoizingMRUCache<string, Type>((type,_) => {
            return Type.GetType(type, false);
        }, 20);

        public static Type ReallyFindType(string type, bool throwOnFailure) 
        {
            lock (typeCache) {
                var ret = typeCache.Get(type);
                if (ret != null || !throwOnFailure) return ret;
                throw new TypeLoadException();
            }
        }
    
        public static Type GetEventArgsTypeForEvent(Type type, string eventName)
        {
            var ti = type;
            var ei = ti.GetRuntimeEvent(eventName);
            if (ei == null) {
                throw new Exception(String.Format("Couldn't find {0}.{1}", type.FullName, eventName));
            }
    
            // Find the EventArgs type parameter of the event via digging around via reflection
            var eventArgsType = ei.EventHandlerType.GetRuntimeMethods().First(x => x.Name == "Invoke").GetParameters()[1].ParameterType;
            return eventArgsType;
        }

        public static void ThrowIfMethodsNotOverloaded(string callingTypeName, object targetObject, params string[] methodsToCheck)
        {
            var missingMethod = methodsToCheck
                .Select(x => {
                    var methods = targetObject.GetType().GetTypeInfo().DeclaredMethods;
                    return Tuple.Create(x, methods.FirstOrDefault(y => y.Name == x));
                })
                .FirstOrDefault(x => x.Item2 == null);

            if (missingMethod != null) {
                throw new Exception(String.Format("Your class must implement {0} and call {1}.{0}", missingMethod.Item1, callingTypeName));
            }
        }

        internal static IObservable<TProp> ViewModelWhenAnyValue<TView, TViewModel, TProp>(TViewModel viewModel, TView view, Expression<Func<TViewModel, TProp>> property)
            where TView : IViewFor
            where TViewModel : class
        {
            return view.WhenAnyValue(x => x.ViewModel)
                .Where(x => x != null)
                .Select(x => ((TViewModel)x).WhenAnyValue(property))
                .Switch();
        }

        internal static IObservable<object> ViewModelWhenAnyValueDynamic<TView, TViewModel>(TViewModel viewModel, TView view, string[] property)
            where TView : IViewFor
            where TViewModel : class
        {
            return view.WhenAny(x => x.ViewModel, x => x.Value)
                .Where(x => x != null)
                .Select(x => property.Length == 0 ? Observable.Return(x) : ((TViewModel)x).WhenAnyDynamic(property, y => y.Value))
                .Switch();
        }

        internal static string getViewPropChain(object view, string[] vmPropChain)
        {
            var vmPropertyName = vmPropChain.Last();
            var getter = GetValueFetcherForProperty(view.GetType(), vmPropertyName);
            if (getter == null)
            {
                throw new Exception(String.Format("Tried to bind to control but it wasn't present on the object: {0}.{1}",
                    view.GetType().FullName, vmPropertyName));
            }

            return vmPropertyName;
        }

        internal static string[] getViewPropChainWithDefault(object view, string[] vmPropChain)
        {
            var viewPropChain = getViewPropChain(view, vmPropChain);

            var control = GetValueFetcherForProperty(view.GetType(), viewPropChain)(view);

            if (control == null) {
                throw new Exception(String.Format("Tried to bind to control but it was null: {0}.{1}", view.GetType().FullName,
                    viewPropChain));
            }

            var defaultProperty = DefaultPropertyBinding.GetPropertyForControl(control);
            if (defaultProperty == null) {
                throw new Exception(String.Format("Couldn't find a default property for type {0}", control.GetType()));
            }
            return new[] { viewPropChain, defaultProperty};
        }
    }

    public static class ReflectionExtensions
    {
        public static bool IsStatic(this PropertyInfo This)
        {
            return (This.GetMethod ?? This.SetMethod).IsStatic;
        }
    }
}
