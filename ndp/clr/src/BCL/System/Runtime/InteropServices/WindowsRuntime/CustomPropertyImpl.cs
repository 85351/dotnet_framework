// ==++==
// 
//   Copyright (c) Microsoft Corporation.  All rights reserved.
// 
// ==--==
//
// <OWNER>[....]</OWNER>

using System;
using System.Security;
using System.Reflection;
using System.Diagnostics.Contracts;
using System.Diagnostics.Tracing;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.StubHelpers;
using System.Globalization;

namespace System.Runtime.InteropServices.WindowsRuntime
{
    //
    // ICustomProperty implementation - basically a wrapper of PropertyInfo
    //
    internal sealed class CustomPropertyImpl : ICustomProperty
    {     
        private PropertyInfo  m_property;

        //
        // Constructor
        //
        public CustomPropertyImpl(PropertyInfo propertyInfo)
        {
            if (propertyInfo == null)
                throw new ArgumentNullException("propertyInfo");

            m_property = propertyInfo;
        }

        //
        // ICustomProperty interface implementation
        //

        public string Name
        {
            get
            {
                return m_property.Name;
            }
        }
        
        public bool CanRead
        {
            get 
            { 
                // Return false if the getter is not public
                return m_property.GetGetMethod() != null;
            }
        }

        public bool CanWrite
        {
            get 
            {
                // Return false if the setter is not public
                return m_property.GetSetMethod() != null;
            }
        }

        public object GetValue(object target)
        {
#if !FEATURE_CORECLR
            if (FrameworkEventSource.IsInitialized && FrameworkEventSource.Log.IsEnabled(EventLevel.Informational, FrameworkEventSource.Keywords.DynamicTypeUsage))
            {
                FrameworkEventSource.Log.BeginCustomPropertyImplGetValue(target != null ? target.GetType().GetFullNameForEtw() : "", m_property.GetFullNameForEtw());
            }
#endif
            object o = InvokeInternal(target, null, true);
#if !FEATURE_CORECLR
            if (FrameworkEventSource.IsInitialized && FrameworkEventSource.Log.IsEnabled(EventLevel.Informational, FrameworkEventSource.Keywords.DynamicTypeUsage))
            {
                FrameworkEventSource.Log.EndCustomPropertyImplGetValue(target != null ? target.GetType().GetFullNameForEtw() : "", m_property.GetFullNameForEtw());
            }
#endif
            return o;
        }

        // Unlike normal .Net, Jupiter properties can have at most one indexer parameter. A null
        // indexValue here means that the property has an indexer argument and its value is null.
        public object GetValue(object target, object indexValue)
        {
#if !FEATURE_CORECLR            
            if (FrameworkEventSource.IsInitialized && FrameworkEventSource.Log.IsEnabled(EventLevel.Informational, FrameworkEventSource.Keywords.DynamicTypeUsage))
            {
                FrameworkEventSource.Log.BeginCustomPropertyImplGetValueIndexValue(target != null ? target.GetType().GetFullNameForEtw() : "", indexValue != null ? indexValue.GetType().GetFullNameForEtw() : "");
            }
#endif
            object o = InvokeInternal(target, new object[] { indexValue }, true);
#if !FEATURE_CORECLR
            if (FrameworkEventSource.IsInitialized && FrameworkEventSource.Log.IsEnabled(EventLevel.Informational, FrameworkEventSource.Keywords.DynamicTypeUsage))
            {
                FrameworkEventSource.Log.EndCustomPropertyImplGetValueIndexValue(target != null ? target.GetType().GetFullNameForEtw() : "", indexValue != null ? indexValue.GetType().GetFullNameForEtw() : "");
            }
#endif
            return o;
        }

        public void SetValue(object target, object value)
        {
#if !FEATURE_CORECLR
            if (FrameworkEventSource.IsInitialized && FrameworkEventSource.Log.IsEnabled(EventLevel.Informational, FrameworkEventSource.Keywords.DynamicTypeUsage))
            {
                FrameworkEventSource.Log.BeginCustomPropertyImplSetValue(target != null ? target.GetType().GetFullNameForEtw() : "", value != null ? value.GetType().GetFullNameForEtw() : "");
            }
#endif
            InvokeInternal(target, new object[] { value }, false);
#if !FEATURE_CORECLR
            if (FrameworkEventSource.IsInitialized && FrameworkEventSource.Log.IsEnabled(EventLevel.Informational, FrameworkEventSource.Keywords.DynamicTypeUsage))
            {
                FrameworkEventSource.Log.EndCustomPropertyImplSetValue(target != null ? target.GetType().GetFullNameForEtw() : "", value != null ? value.GetType().GetFullNameForEtw() : "");
            }
#endif
        }

        // Unlike normal .Net, Jupiter properties can have at most one indexer parameter. A null
        // indexValue here means that the property has an indexer argument and its value is null.
        public void SetValue(object target, object value, object indexValue)
        {
#if !FEATURE_CORECLR
            if (FrameworkEventSource.IsInitialized && FrameworkEventSource.Log.IsEnabled(EventLevel.Informational, FrameworkEventSource.Keywords.DynamicTypeUsage))
            {
                FrameworkEventSource.Log.BeginCustomPropertyImplSetValueIndexValue(target != null ? target.GetType().GetFullNameForEtw() : "", value != null ? value.GetType().GetFullNameForEtw() : "", indexValue != null ? indexValue.GetType().GetFullNameForEtw() : "");
            }
#endif
            InvokeInternal(target, new object[] { indexValue, value }, false);
#if !FEATURE_CORECLR
            if (FrameworkEventSource.IsInitialized && FrameworkEventSource.Log.IsEnabled(EventLevel.Informational, FrameworkEventSource.Keywords.DynamicTypeUsage))
            {
                FrameworkEventSource.Log.EndCustomPropertyImplSetValueIndexValue(target != null ? target.GetType().GetFullNameForEtw() : "", value != null ? value.GetType().GetFullNameForEtw() : "", indexValue != null ? indexValue.GetType().GetFullNameForEtw() : "");
            }
#endif

        }

        [SecuritySafeCritical]
        private object InvokeInternal(object target, object[] args, bool getValue)
        {
            // Forward to the right object if we are dealing with a proxy
            IGetProxyTarget proxy = target as IGetProxyTarget;
            if (proxy != null)
            {
                target = proxy.GetTarget();
            }

            // You can get PropertyInfo for properties with a private getter/public setter (or vice versa) 
            // even if you pass BindingFlags.Public only. And in this case, passing binding flags to 
            // GetValue/SetValue won't work as the default binder ignores those values
            // Use GetGetMethod/GetSetMethod instead

            // We get non-public accessors just so that we can throw the correct exception.
            MethodInfo accessor = getValue ? m_property.GetGetMethod(true) : m_property.GetSetMethod(true);
            
            if (accessor == null)
                throw new ArgumentException(System.Environment.GetResourceString(getValue ? "Arg_GetMethNotFnd" : "Arg_SetMethNotFnd"));

            if (!accessor.IsPublic)
                throw new MethodAccessException(
                    String.Format(
                        CultureInfo.CurrentCulture,
                        Environment.GetResourceString("Arg_MethodAccessException_WithMethodName"),
                        accessor.ToString(),
                        accessor.DeclaringType.FullName));

            RuntimeMethodInfo rtMethod = accessor as RuntimeMethodInfo;
            if (rtMethod == null)
                throw new ArgumentException(Environment.GetResourceString("Argument_MustBeRuntimeMethodInfo"));

            // We can safely skip access check because this is only used in full trust scenarios.
            // And we have already verified that the property accessor is public.
            Contract.Assert(AppDomain.CurrentDomain.PermissionSet.IsUnrestricted());
            return rtMethod.UnsafeInvoke(target, BindingFlags.Default, null, args, null);
        }

        public Type Type
        {
            get
            {
                return m_property.PropertyType;
            }
        }
    }
}
