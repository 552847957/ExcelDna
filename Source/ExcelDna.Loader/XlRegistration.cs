﻿/*
  Copyright (C) 2005-2014 Govert van Drimmelen

  This software is provided 'as-is', without any express or implied
  warranty.  In no event will the authors be held liable for any damages
  arising from the use of this software.

  Permission is granted to anyone to use this software for any purpose,
  including commercial applications, and to alter it and redistribute it
  freely, subject to the following restrictions:

  1. The origin of this software must not be misrepresented; you must not
     claim that you wrote the original software. If you use this software
     in a product, an acknowledgment in the product documentation would be
     appreciated but is not required.
  2. Altered source versions must be plainly marked as such, and must not be
     misrepresented as being the original software.
  3. This notice may not be removed or altered from any source distribution.


  Govert van Drimmelen
  govert@icon.co.za
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;

namespace ExcelDna.Loader
{
    // TODO: Migrate registration to ExcelDna.Integration
    public static class XlRegistration
    {
        static readonly List<XlMethodInfo> registeredMethods = new List<XlMethodInfo>();
        static readonly List<string> addedShortCuts = new List<string>();
        
        // This list is just to give access to the registration details for UI enhancement.
        // Each entry corresponds exactly to the xlfRegister call (except first entry with xllPath is cleared) 
        // - max length of each array is 255.
        static readonly List<object[]> registrationInfo = new List<object[]>();
        static double registrationInfoVersion = 0.0; // Incremented every time the registration changes, used by GetRegistrationInfo to short-circuit.
        
        public static void RegisterMethods(List<MethodInfo> methods)
        {
            List<object> methodAttributes;
            List<List<object>> argumentAttributes;
            XlMethodInfo.GetMethodAttributes(methods, out methodAttributes, out argumentAttributes);
            RegisterMethodsWithAttributes(methods, methodAttributes, argumentAttributes);
        }

        public static void RegisterMethodsWithAttributes(List<MethodInfo> methods, List<object> methodAttributes, List<List<object>> argumentAttributes)
        {
            Register(methods, null,  methodAttributes, argumentAttributes);
        }

        public static void RegisterDelegatesWithAttributes(List<Delegate> delegates, List<object> methodAttributes, List<List<object>> argumentAttributes)
        {
            // I'm missing LINQ ...
            List<MethodInfo> methods = new List<MethodInfo>();
            List<object> targets = new List<object>();
            for (int i = 0; i < delegates.Count; i++)
            {
                Delegate del = delegates[i];
                // Using del.Method and del.Target from here is a problem 
                // - then we have to deal with the open/closed situation very carefully.
                // We'll pass and invoke the actual delegate, which means the method signature is correct.
                // Overhead should be negligible.
                methods.Add(del.GetType().GetMethod("Invoke"));
                targets.Add(del);
            }
            Register(methods, targets, methodAttributes, argumentAttributes);
        }

        // To keep everything alive ???
        static List<RtdWrapperHelper> _rtdWrapperHelpers;
        public static void RegisterRtdWrapper(string progId, object rtdWrapperOptions, object functionAttribute, List<object> argumentAttributes)
        {
            if (_rtdWrapperHelpers == null)
                _rtdWrapperHelpers = new List<RtdWrapperHelper>();

            var helper = new RtdWrapperHelper(progId, rtdWrapperOptions);
            _rtdWrapperHelpers.Add(helper);

            // var del = Delegate.CreateDelegate(typeof(RtdWrapperHelper.RtdWrapperDelegate, helper, "RtdWrapperHelper");

            // TODO: Need to check that we are not marked as IsTreadSafe (for now)

            var rtdWrapperMethod = RtdWrapperHelper.GetRtdWrapperMethod();
            Register(new List<MethodInfo> { rtdWrapperMethod },
                     new List<object> { helper },
                     new List<object> { functionAttribute },
                     new List<List<object>> { argumentAttributes });
        }

        // This function provides access to the registration info from an IntelliSense provider.
        // To allow polling, we return as the first row a (double) version which can be passed to short-circuit the call if nothing has changed.
        // The signature and behaviour should be flexible enough to allow future non-breaking extension.
        public static object GetRegistrationInfo(object param)
        {
            if (param is double && (double)param == registrationInfoVersion)
            {
                // Short circuit, to prevent returning the whole string story every time, allowing fast polling.
                return null;
            }

            // Copy from the jagged List to a 2D array with 255 columns
            // (missing bits are returned as null, which is marshaled to XlEmpty)
            object[,] result = new object[registrationInfo.Count + 1, 255];
            // Return xll path and registrationVersion in first row
            result[0, 0] = XlAddIn.PathXll;
            result[0, 1] = registrationInfoVersion;

            // Other rows contain the registation info 
            for (int i = 0; i < registrationInfo.Count; i++)
            {
                int resultRow = i + 1;
                object[] info = registrationInfo[i];
                for (int j = 0; j < 255; j++)
                {
                    if (j >= info.Length)
                    {
                        // Done with this row
                        break;
                    }
                    result[resultRow, j] = info[j];
                }
            }
            return result;
        }

        static void Register(List<MethodInfo> methods, List<object> targets, List<object> methodAttributes, List<List<object>> argumentAttributes)
        {
            Debug.Assert(targets == null || targets.Count == methods.Count);

            List<XlMethodInfo> xlMethods = XlMethodInfo.ConvertToXlMethodInfos(methods, targets, methodAttributes, argumentAttributes);
            xlMethods.ForEach(RegisterXlMethod);
            // Increment the registration version (safe to call a few times)
            registrationInfoVersion += 1.0;
        }

        private static void RegisterXlMethod(XlMethodInfo mi)
        {
            int index = registeredMethods.Count;
            XlAddIn.SetJump(index, mi.FunctionPointer);
            String exportedProcName = String.Format("f{0}", index);

            object[] registerParameters = GetRegisterParameters(mi, exportedProcName);
            
            // Basically suppress problems here !?
            try
            {
                object xlCallResult;
                XlCallImpl.TryExcelImpl(XlCallImpl.xlfRegister, out xlCallResult, registerParameters);
                Debug.Print("Register - XllPath={0}, ProcName={1}, FunctionType={2}, MethodName={3} - Result={4}",
                            registerParameters[0], registerParameters[1], registerParameters[2], registerParameters[3],
                            xlCallResult);
                if (xlCallResult is double)
                {
                    mi.RegisterId = (double) xlCallResult;
                    registeredMethods.Add(mi);
                    if (mi.IsCommand)
                    {
                        RegisterMenu(mi);
                        RegisterShortCut(mi);
                    }
                }
                else
                {
                    // TODO: What to do here? LogDisplay??
                    Debug.Print("Registration Error! - Register call failed for method {0}", mi.Name);
                }
                // Now clear out the xll path and store the parameters to support RegistrationInfo access.
                registerParameters[0] = null;
                registrationInfo.Add(registerParameters);
            }
            catch (Exception e)
            {
                // TODO: What to do here? LogDisplay??
                Debug.WriteLine("Registration Error! - " + e.Message);
            }
        }

        // NOTE: We are not currently removing the functions from the Jmp array
        //       That would be needed to do a proper per-method deregistration,
        //       together with a garbage-collectable story for the wrapper methods and delegates, 
        //       instead of the currently runtime-compiled and loaded assemblies.
        internal static void UnregisterMethods()
        {
            object xlCallResult;

            // Remove menus and ShortCuts
            IntegrationHelpers.RemoveCommandMenus();
            UnregisterShortCuts();

            // Now take out the methods
            foreach (XlMethodInfo mi in registeredMethods)
            {
                if (mi.IsCommand)
                {
                    // Clear the name and unregister
                    XlCallImpl.TryExcelImpl(XlCallImpl.xlfSetName, out xlCallResult, mi.Name);
                    XlCallImpl.TryExcelImpl(XlCallImpl.xlfUnregister, out xlCallResult, mi.RegisterId);
                }
                else
                {
                    // And Unregister the real function
                    XlCallImpl.TryExcelImpl(XlCallImpl.xlfUnregister, out xlCallResult, mi.RegisterId);
                    // I follow the advice from X-Cell website to get function out of Wizard (with fix from kh)
                    XlCallImpl.TryExcelImpl(XlCallImpl.xlfRegister, out xlCallResult, XlAddIn.PathXll, "xlAutoRemove", "I", mi.Name, IntegrationMarshalHelpers.GetExcelMissingValue(), 2);
                    if (xlCallResult is double)
                    {
                        double fakeRegisterId = (double)xlCallResult;
                        XlCallImpl.TryExcelImpl(XlCallImpl.xlfSetName, out xlCallResult, mi.Name);
                        XlCallImpl.TryExcelImpl(XlCallImpl.xlfUnregister, out xlCallResult, fakeRegisterId);
                    }
                }
            }
            registeredMethods.Clear();
            registrationInfo.Clear();
        }

        private static void RegisterMenu(XlMethodInfo mi)
        {
            if (!string.IsNullOrEmpty(mi.MenuName) &&
                !string.IsNullOrEmpty(mi.MenuText))
            {
                IntegrationHelpers.AddCommandMenu(mi.Name, mi.MenuName, mi.MenuText, mi.Description, mi.ShortCut, mi.HelpTopic);
            }
        }

        private static void RegisterShortCut(XlMethodInfo mi)
        {
            if (!string.IsNullOrEmpty(mi.ShortCut))
            {
                object xlCallResult;
                XlCallImpl.TryExcelImpl(XlCallImpl.xlcOnKey, out xlCallResult, mi.ShortCut, mi.Name);
                // CONSIDER: We ignore result and suppress errors - maybe log?
                addedShortCuts.Add(mi.ShortCut);
            }
        }

        private static void UnregisterShortCuts()
        {
            foreach (string shortCut in addedShortCuts)
            {
                // xlcOnKey with no macro name:
                // "If macro_text is omitted, key_text reverts to its normal meaning in Microsoft Excel, 
                // and any special key assignments made with previous ON.KEY functions are cleared."
                object xlCallResult;
                XlCallImpl.TryExcelImpl(XlCallImpl.xlcOnKey, out xlCallResult, shortCut);
            }
        }

        private static object[] GetRegisterParameters(XlMethodInfo mi, string exportedProcName)
        {
            string functionType;
            if (mi.ReturnType != null)
            {
                functionType = mi.ReturnType.XlType;
            }
            else
            {
                if (mi.Parameters.Length == 0)
                {
                    functionType = "";  // OK since no other types will be added
                }
                else
                {
                    // This case is also be used for native async functions
                    functionType = ">"; // Use the void / inplace indicator if needed.
                }
            }

            string argumentNames = "";
            bool showDescriptions = false;
            // For async functions, we need to leave off the last argument
            int numArgumentDescriptions = mi.IsExcelAsyncFunction ? mi.Parameters.Length - 1 : mi.Parameters.Length;
            string[] argumentDescriptions = new string[numArgumentDescriptions];

            for (int j = 0; j < numArgumentDescriptions; j++)
            {
                XlParameterInfo pi = mi.Parameters[j];

                functionType += pi.XlType;

                if (j > 0)
                    argumentNames += ",";   // TODO: Should this be a comma, or the Excel list separator?
                argumentNames += pi.Name;
                argumentDescriptions[j] = pi.Description;

                if (pi.Description != "")
                    showDescriptions = true;

                // DOCUMENT: Truncate the argument description if it exceeds the Excel limit of 255 characters
                if (j < mi.Parameters.Length - 1)
                {
                    if (!string.IsNullOrEmpty(argumentDescriptions[j]) &&
                        argumentDescriptions[j].Length > 255)
                    {
                        argumentDescriptions[j] = argumentDescriptions[j].Substring(0, 255);
                        Debug.Print("Truncated argument description of {0} in method {1} as Excel limit was exceeded",
                                    pi.Name, mi.Name);
                    }
                }
                else
                {
                    // Last argument - need to deal with extra ". "
                    if (!string.IsNullOrEmpty(argumentDescriptions[j]))
                    {
                        if (argumentDescriptions[j].Length > 253)
                        {
                            argumentDescriptions[j] = argumentDescriptions[j].Substring(0, 253);
                            Debug.Print("Truncated field description of {0} in method {1} as Excel limit was exceeded",
                                        pi.Name, mi.Name);
                        }

                        // DOCUMENT: Here is the patch for the Excel Function Description bug.
                        // DOCUMENT: I add ". " to the last parameter.
                        argumentDescriptions[j] += ". ";
                    }
                }
            } // for each parameter

            // Add async handle
            if (mi.IsExcelAsyncFunction)
                functionType += "X"; // mi.Parameters[mi.Parameters.Length - 1].XlType should be "X" anyway

            // Native async functions cannot be cluster safe
            if (mi.IsClusterSafe && ProcessHelper.SupportsClusterSafe && !mi.IsExcelAsyncFunction)
                functionType += "&";

            if (mi.IsMacroType)
                functionType += "#";

            if (!mi.IsMacroType && mi.IsThreadSafe && XlAddIn.XlCallVersion >= 12)
                functionType += "$";

            if (mi.IsVolatile)
                functionType += "!";
            // DOCUMENT: If # is set and there is an R argument, Excel considers the function volatile anyway.
            // You can call xlfVolatile, false in beginning of function to clear.

            string functionDescription = mi.Description;
            // DOCUMENT: Truncate Description to 253 characters (for all versions)
            functionDescription = Truncate(functionDescription, 253);

            // DOCUMENT: Here is the patch for the Excel Function Description bug.
            // DOCUMENT: I add ". " if the function takes no parameters and has a description.
            if (mi.Parameters.Length == 0 && functionDescription != "")
                functionDescription += ". ";

            // DOCUMENT: When there is no description, we don't add any.
            // This allows the user to work around the Excel bug where an extra parameter is displayed if
            // the function has no parameter but displays a description
            if (mi.Description != "")
                showDescriptions = true;

            int numRegisterParameters;
            // DOCUMENT: Maximum 20 Argument Descriptions when registering using Excel4 function.
            int maxDescriptions = (XlAddIn.XlCallVersion < 12) ? 20 : 245;
            if (showDescriptions)
            {
                numArgumentDescriptions = Math.Min(numArgumentDescriptions, maxDescriptions);
                numRegisterParameters = 10 + numArgumentDescriptions;    // function description + arg descriptions
            }
            else
            {
                // Won't be showing any descriptions.
                numArgumentDescriptions = 0;
                numRegisterParameters = 9;
            }

            // DOCUMENT: Additional truncations of registration info - registration fails with strings longer than 255 chars.
            argumentNames = Truncate(argumentNames, 255);
            argumentNames = argumentNames.TrimEnd(','); // Also trim trailing commas (for params case)
            string category = Truncate(mi.Category, 255);
            string name = Truncate(mi.Name, 255);
            string helpTopic = (mi.HelpTopic == null || mi.HelpTopic.Length <= 255) ? mi.HelpTopic : "";

            object[] registerParameters = new object[numRegisterParameters];
            registerParameters[0] = XlAddIn.PathXll;
            registerParameters[1] = exportedProcName;
            registerParameters[2] = functionType;
            registerParameters[3] = name;
            registerParameters[4] = argumentNames;
            registerParameters[5] = mi.IsCommand ? 2 /*macro*/
                                                 : (mi.IsHidden ? 0 : 1); /*function*/
            registerParameters[6] = category;
            registerParameters[7] = mi.ShortCut; /*shortcut_text*/
            registerParameters[8] = helpTopic; /*help_topic*/

            if (showDescriptions)
            {
                registerParameters[9] = functionDescription;

                for (int k = 0; k < numArgumentDescriptions; k++)
                {
                    registerParameters[10 + k] = argumentDescriptions[k];
                }
            }

            return registerParameters;
        }

        static string Truncate(string s, int length)
        {
            if (s == null || s.Length <= length) return s;
            return s.Substring(0, length);
        }
    }
}
