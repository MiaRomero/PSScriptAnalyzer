// Copyright (c) Microsoft Corporation.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using System;
using System.Collections.Generic;
#if !CORECLR
using System.ComponentModel.Composition;
#endif
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management.Automation.Language;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Windows.PowerShell.ScriptAnalyzer.Generic;
using Newtonsoft.Json.Linq;

namespace Microsoft.Windows.PowerShell.ScriptAnalyzer.BuiltinRules
{
    /// <summary>
    /// A class to check if a script uses types compatible with a given version and edition of PowerShell (and .NET)
    /// </summary>
    #if !CORECLR
    [Export(typeof(IScriptRule))]
    #endif

    public class UseCompatibleTypes : IScriptRule 
    {
        // List of all diagnostic records for incompatible types.
        private List<DiagnosticRecord> diagnosticRecords;

        // List of full type names for each target platform.
        private Dictionary<string, HashSet<string>> psTypeMap;

        // List of full type names from desktop PowerShell.
        private HashSet<string> referenceMap;

        // List of type accelerator names and their corresponding full names.
        private Dictionary<string, string> typeAcceleratorMap;

        // Parameters valid for this rule.
        private readonly string validParameters;

        // Lists each target platform (broken down into edition, version, os).
        private Dictionary<string, dynamic> platformSpecMap;

        // Path of script being analyzed by ScriptAnalyzer.
        private string scriptPath;

        // List of all TypeAst objects (TypeConstraintAst, TypeExpressionAst, and 
        // types used with 'New-Object') found in ast.
        private List<dynamic> allTypesFromAst;

        // List of .Net namespaces (first word only).
        private List<string> knownNamespaces = new List<string> {"System.", "Microsoft.", "Newtonsoft.", "Internal."};

        // List of user created types found in ast (TypeDefinitionAst).
        private List<string> customTypes;

        private bool IsInitialized;
        private bool hasInitializationError;

        private struct fullTypeNameObject
        {
            public string fullName;
            public bool isCustomType;
            public bool cannotBeResolved;
            public bool isTypeAccelerator;

            public fullTypeNameObject(bool customType)
            {
                fullName = null;
                isCustomType = customType;
                cannotBeResolved = false;
                isTypeAccelerator = false;
            }
        }

        public UseCompatibleTypes()
        {
            validParameters = "compatibility";
            IsInitialized = false;
        }

        /// <summary>
        /// Retrieves the common name of this rule.
        /// </summary>
        public string GetCommonName()
        {
            return string.Format(CultureInfo.CurrentCulture, Strings.UseCompatibleTypesCommonName);
        }

        /// <summary>
        /// Retrieves the description of this rule.
        /// </summary>
        public string GetDescription()
        {
            return string.Format(CultureInfo.CurrentCulture, Strings.UseCompatibleTypesDescription);
        }

        /// <summary>
        /// Retrieves the name of this rule.
        /// </summary>
        public string GetName()
        {
            return string.Format(
                CultureInfo.CurrentCulture,
                Strings.NameSpaceFormat,
                GetSourceName(),
                Strings.UseCompatibleTypesName);
        }

        /// <summary>
        /// Retrieves the severity of the rule: error, warning, or information.
        /// </summary>
        public RuleSeverity GetSeverity()
        {
            return RuleSeverity.Error;
        }

        /// <summary>
        /// Gets the severity of the returned diagnostic record: error, warning, or information.
        /// </summary>
        public DiagnosticSeverity GetDiagnosticSeverity()
        {
            return DiagnosticSeverity.Error;
        }

        /// <summary>
        /// Retrieves the name of the module/assembly the rule is from.
        /// </summary>
        public string GetSourceName()
        {
            return string.Format(CultureInfo.CurrentCulture, Strings.SourceName);
        }

        /// <summary>
        /// Retrieves the type of the rule, Builtin, Managed, or Module.
        /// </summary>
        public SourceType GetSourceType()
        {
            return SourceType.Builtin;
        }

        /// <summary>
        /// Analyzes the given ast to find the violation(s).
        /// </summary>
        /// <param name="ast">AST to be analyzed. This should be non-null.</param>
        /// <param name="fileName">Name of file that corresponds to the input AST.</param>
        /// <returns>An enumerable type containing the violations.</returns>
        public IEnumerable<DiagnosticRecord> AnalyzeScript(Ast ast, string fileName)
        {
            if (ast == null)
            {
                throw new ArgumentNullException("ast");
            }

            // We do not want to initialize the data structures if the rule is not being used for analysis,
            // hence we initialize when this method is called for the first time.
            if (!IsInitialized)
            {
                Initialize();
            }

            if (hasInitializationError)
            {
                return new DiagnosticRecord[0];
            }

            diagnosticRecords.Clear();

            scriptPath = fileName;
            allTypesFromAst = new List<dynamic>();
            customTypes = new List<string>();
            
            IEnumerable<Ast> constraintAsts = ast.FindAll(testAst => testAst is TypeConstraintAst, true);
            addAstElementToList(constraintAsts);

            IEnumerable<Ast> expressionAsts = ast.FindAll(testAst => testAst is TypeExpressionAst, true);
            addAstElementToList(expressionAsts);

            // Here we are looking for types named within a command.  We will want only types 
            // used with the 'New-Object' cmdlet, but we will filter these out later.
            IEnumerable<Ast> commandAsts = ast.FindAll(testAst => testAst is CommandAst, true);
            addAstElementToList(commandAsts); 

            // These are user-created types (defined within a user-created class).
            IEnumerable<Ast> definitionAsts = ast.FindAll(testAst => testAst is TypeDefinitionAst, true);
            foreach (Ast item in definitionAsts)
            {
                string customType = item.GetType().GetProperty("Name").GetValue(item).ToString();
                customTypes.Add(customType);
            }

            // These are objects cast to a type.
            IEnumerable<Ast> convertAsts = ast.FindAll(testAst => testAst is ConvertExpressionAst, true);
            foreach (Ast item in convertAsts)
            {
                string convertType = item.GetType().GetProperty("StaticType").GetValue(item).ToString();
                if (convertType.Contains("System.Type"))
                {
                    string customType = item.GetType().GetProperty("Child")?.GetValue(item).ToString();
                   
                    // If customType is a variable, we cannot resolve it so ignore it, otherwise add the type to
                    // our custom types list.
                    if (customType != null && !(customType.StartsWith("$")))
                    {
                        customTypes.Add(customType);
                    }
                    
                }
            }

            // If we have no types to check, we can exit from this rule.
            if (allTypesFromAst.Count == 0)
            {
                return new DiagnosticRecord[0];
            }
    
            CheckCompatibility();

            return diagnosticRecords;
        }

         /// <summary>
        /// Adds the found ast objects to the 'master' type list (allTypesFromAst).
        /// </summary>
        private void addAstElementToList(dynamic astList)
        {
            foreach(dynamic foundAst in astList)
            {
                allTypesFromAst.Add(foundAst);
            }
        }

        /// <summary>
        /// Check if rule parameters are valid (at least one parameter must be 'compatibility').
        /// </summary>
        private bool RuleParamsValid(Dictionary<string, object> ruleArgs)
        {
            return ruleArgs.Keys.Any(key => validParameters.Equals(key, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Search the Settings directory for files of form [PSEdition]-[PSVersion]-[OS].json.
        /// For each json file found that matches our target platforms, parse file to create type map.
        /// </summary>
        private void ProcessDirectory(string path, IEnumerable<string> acceptablePlatformSpecs)
        {
            var jsonFiles = Directory.EnumerateFiles(path, "*.json");
            if (jsonFiles == null)
            {
                return;
            }

            foreach (var file in jsonFiles)
            {
                var fileNameWithoutExt = Path.GetFileNameWithoutExtension(file);
                if (acceptablePlatformSpecs != null
                    && !acceptablePlatformSpecs.Contains(fileNameWithoutExt, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }
                dynamic deserialized = JObject.Parse(File.ReadAllText(file));
                psTypeMap[fileNameWithoutExt] = GetTypesFromData(deserialized);
            }
        }

        /// <summary>
        /// Set up the reference type map (from the latest desktop version).
        /// </summary>
        private void SetUpReferenceMap(string path)
        {
            var referenceFile = Directory.GetFiles(path, "desktop-5.1*");
            dynamic deserialized = JObject.Parse(File.ReadAllText(referenceFile[0]));
            referenceMap = GetTypesFromData(deserialized);
        }

        /// <summary>
        /// Get a hashset of full type names from a deserialized json file.
        /// </summary>
        private HashSet<string> GetTypesFromData(dynamic deserializedObject)
        {
            HashSet<string> types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            dynamic typeList = deserializedObject.Types;
            foreach (dynamic type in typeList)
            {   
                string name = type.Name.ToString();
                string nameSpace = type.Namespace.ToString();       
                string fullName = nameSpace + "." + name;
                types.Add(fullName);
            }           
            return types;
        }

        /// <summary>
        /// Create the type accelerator map from json file in Settings directory.
        /// </summary>
        private void CreateTypeAcceleratorMap(string path)
        {
            var typeAccFile = Path.Combine(path, "typeAccelerators.json");
            dynamic deserialized = JObject.Parse(File.ReadAllText(typeAccFile));

            foreach (dynamic typeAcc in deserialized)
            {
                typeAcceleratorMap.Add(typeAcc.Name.ToString(), typeAcc.Value.ToString());
            }
        }

        /// <summary>
        /// Gets PowerShell Edition, Version and OS from input string.
        /// </summary>
        /// <returns>True if it can retrieve information from string, otherwise, False</returns>
        private bool GetVersionInfoFromPlatformString(
            string fileName,
            out string psedition,
            out string psversion,
            out string os)
        {
            psedition = null;
            psversion = null;
            os = null;
            const string pattern = @"^(?<psedition>core|desktop)-(?<psversion>[\S]+)-(?<os>windows|linux|osx|nano|iot)$";
            var match = Regex.Match(fileName, pattern, RegexOptions.IgnoreCase);
            if (match == Match.Empty)
            {
                return false;
            }
            psedition = match.Groups["psedition"].Value;
            psversion = match.Groups["psversion"].Value;
            os = match.Groups["os"].Value;
            return true;
        }

        /// <summary>
        /// Sets up dictionaries indexed by PowerShell version/edition and OS; and
        /// sets up type accelerator dictionary.
        /// </summary>
        private void SetupTypesDictionary()
        {
            // If the method encounters any error it returns early,
            // which implies there is an initialization error.
            hasInitializationError = true;

            // Retrieve rule parameters provided by user.
            Dictionary<string, object> ruleArgs = Helper.Instance.GetRuleArguments(GetName());

            // If there are no params or if none are 'compatibility', return.
            if (ruleArgs == null || !RuleParamsValid(ruleArgs))
            {
                return;
            }

            // For each target platform listed in the 'compatibility' param, add it to compatibilityList.
            var compatibilityObjectArr = ruleArgs["compatibility"] as object[];
            var compatibilityList = new List<string>();
            if (compatibilityObjectArr == null)
            {
                compatibilityList = ruleArgs["compatibility"] as List<string>;
                if (compatibilityList == null)
                {
                    return;
                }
            }
            else
            {
                foreach (var compatItem in compatibilityObjectArr)
                {
                    var compatString = compatItem as string;
                    if (compatString == null)
                    {
                        // ignore (warn) non-string/invalid entries
                        continue;
                    }
                    compatibilityList.Add(compatString);
                }
            }

            // Create our platformSpecMap from the target platforms in the compatibilityList.
            foreach (var compat in compatibilityList)
            {
                string psedition, psversion, os;

                // ignore (warn) invalid entries
                if (GetVersionInfoFromPlatformString(compat, out psedition, out psversion, out os))
                {
                    platformSpecMap.Add(compat, new { PSEdition = psedition, PSVersion = psversion, OS = os });
                }
            }

            // Get path where the json dictionaries are located.
            string settingsPath = Settings.GetShippedSettingsDirectory();

            if (settingsPath == null)
            {
                return;
            }

            // Find corresponding dictionaries for target platforms and create type maps.
            ProcessDirectory(settingsPath, compatibilityList);
            SetUpReferenceMap(settingsPath);
            CreateTypeAcceleratorMap(settingsPath);

            if (psTypeMap.Keys.Count != compatibilityList.Count())
            {
                return;
            }

            // Reached this point, so no initialization error.
            hasInitializationError = false;
        }

        /// <summary>
        /// Initialize data structures needed to check cmdlet compatibility.
        /// </summary>
        private void Initialize()
        {
            diagnosticRecords = new List<DiagnosticRecord>();
            psTypeMap = new Dictionary<string, HashSet<string>>((StringComparer.OrdinalIgnoreCase));
            referenceMap = new HashSet<string>((StringComparer.OrdinalIgnoreCase));
            typeAcceleratorMap = new Dictionary<string, string>();
            platformSpecMap = new Dictionary<string, dynamic>(StringComparer.OrdinalIgnoreCase);
            SetupTypesDictionary();
            IsInitialized = true;
        }

        /// <summary>
        /// Check to see if type full name includes number of parameters (i.e. `2).
        /// </summary>
        private bool CheckForNumberedParameters (Regex reg)
        {
            foreach (dynamic platform in psTypeMap)
            {
                foreach (string dictionaryTypeName in platform.Value)
                {
                    Match m = reg.Match(dictionaryTypeName);
                    if (m.Success)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        ///<summary>
        /// Check Type Accelerator map for full type name.
        ///</summary>
        private string CheckTypeAcceleratorMap(string typeName)
        {
            typeName = typeName.ToLower();
            string value = null;
            if (typeAcceleratorMap.TryGetValue(typeName, out value))
            {
                return value;
            }
            return value;
        }

        ///<summary>
        /// Check if type name starts with a known namespace.
        ///</summary>
        private bool StartsWithKnownNamespace(string typeName) 
        {
            if (typeName != null)
            {
                foreach (string nspace in knownNamespaces)
                {
                    if (typeName.StartsWith(nspace, StringComparison.OrdinalIgnoreCase))
                    {
                        return true; 
                    }
                }
            }
            return false;
        }

        ///<summary>
        /// Get a full type name from the reference map.
        ///</summary>
        private string TryReferenceMap(string TypeName)
        {
            foreach (string nspace in knownNamespaces)
            {
                string possibleFullName = nspace + TypeName;
                if (referenceMap.Contains(possibleFullName, StringComparer.OrdinalIgnoreCase))
                {
                    return possibleFullName;
                }
            }
            return null;
        }

        ///<summary>
        /// Look up type in target platform dictionary by adding known namespaces 
        /// to the beginning.
        /// </summary>
        private bool TryKnownNameSpaces(string typeName, dynamic platform)
        {
            foreach (string nspace in knownNamespaces)
            {
                string possibleFullName = nspace + typeName;
                if (platform.Value.Contains(possibleFullName))
                {
                    return true;
                }
            }
            return false;
        }

        ///<summary>
        /// Check if type is from a Universal Windows Platform (UWP) namespace. If the typeName 
        /// starts with 'Windows' it does not ALWAYS mean it's from a UWP namespace, but usually it is.  
        /// We are using this check to prevent our type from being labeled 'custom' and therefore giving
        /// the incorrect error message.
        ///</summary>
        private bool PossibleUWPtype(string typeName)
        {
            if (typeName.StartsWith("Windows.", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return false;
        }

        ///<summary>
        /// Retrieve non-public field from ast object.
        ///</summary>
        private string GetAstField (dynamic typeNamePropertyObject, string desiredProperty)
        {
            dynamic property = typeNamePropertyObject.GetType()?.GetField(desiredProperty, 
                               BindingFlags.Instance|BindingFlags.NonPublic)?.GetValue(
                               typeNamePropertyObject);

            if (desiredProperty == "_type" && property != null)
            {
                return property.FullName.ToString();
            }
            else if (property != null)
            {
                return property.ToString();
            }
            else
            {
                return property;
            }
        }

        /// <summary>
        /// Retrieve full type names from a 'New-Object' CommandAst.
        /// </summary>
        private List<fullTypeNameObject> GetFullNameFromNewObjectCommand(dynamic typeObject)
        {
            // Each commandAst object has the CommandElements property that looks like the following:
            // 
            // CommandElements[0] = This is normally the name of the command (i.e. 'Get-Command', 'New-Object').
            //
            // CommandElements[1] = This can either be the object/type of the command OR a parameter name.
            //
            // CommandElements[2] = If CommandElements[1] is a parameter, then this is the object/type 
            //                      (i.e.'string', 'myType').                 
        
            List<fullTypeNameObject> fullNameObjectList = new List<fullTypeNameObject>();
            string typeName = null;

            try 
            {
                // Get only the 'New-Object' commandAsts.
                if (String.Equals(typeObject.GetCommandName().ToString(),"New-Object", StringComparison.OrdinalIgnoreCase))
                {
                    dynamic element = typeObject.CommandElements[1];
                    string elementType = null;

                    // Check to see if 'element' is a parameter.
                    try
                    {
                            elementType = element.ParameterName?.ToString().ToLower();
                            // It is a parameter, so we only want to deal with the -TypeName 
                            // parameter, not -ComObject.
                            if (elementType.Contains("type"))
                            {
                                element = typeObject.CommandElements[2];
                                typeName = element?.Value.ToString();
                            }
                    }
                    catch
                    {
                        // We know it's not a parameter, so get the value.  We want to try and get the Value property
                        // if we can because sometimes the element will contain quotation marks, which we don't want.
                        try
                        {
                            typeName = element.Value.ToString();
                        }
                        catch
                        {
                            typeName = element.ToString();
                        }
                    }
                }

                // Possibility typeName could be an array (string[]) in which case we just want 'string',
                // or more than one type (specialType[string, int]) in which case we want all three types.
                string [] typeNameComponents = typeName.Split(new Char [] {'[', ',', ']', ' ', '\'', '(', ')'}, 
                                                        StringSplitOptions.RemoveEmptyEntries);

                // Some types' full name specify the number of parameters they take.
                // For example:  System.Collections.Generic.List`1
                // Because we are only working with a string after 'New-Object' our ast 
                // object will never give us the full type name with " `1 " in it.  
                // 
                // If there is more than one item in our typeNameComponents array we know we
                // need to check our dictionary for a type that takes parameters.
                // If we find a match, we know what number to put after the ` based on how many items
                // we have in typeNameComponents.  Example:
                //
                //      System.Collections.Generic.SortedList[string, string]
                //      typeNameComponents will have a length of 3, so let's check our dictionary with:
                //      @"System.Collections.Generic.SortedList`\d"
                //      Provided we have a match our full type name would then be:
                //      System.Collections.Generic.SortedList`2.
                //
                // This is not a compatibility check, we are just trying to get a full name for our type.
                
                if (typeNameComponents.Length > 1 && (!typeNameComponents[0].Contains("`")))
                {
                    /*string typeRegex = typeNameComponents[0] + @"`\d";
                    Regex reg = new Regex(typeRegex, RegexOptions.IgnoreCase);

                    if (CheckForNumberedParameters(reg))
                    {*/
                        // find length of typeNameComponents to add to our typeName
                        string parameterNumber = (typeNameComponents.Length - 1).ToString();
                        typeNameComponents[0] = typeNameComponents[0] + "`" + parameterNumber;
                    //}
                }
            
                foreach (string name in typeNameComponents)
                {
                    fullTypeNameObject fullNameObject = new fullTypeNameObject(false);

                    // Is our name already a full name (includes namespace)?
                    if (StartsWithKnownNamespace(name) || PossibleUWPtype(name))
                    {
                        fullNameObject.fullName = name;
                    }
            
                    if (fullNameObject.fullName == null)
                    {
                        // Is this a type accelerator?
                        fullNameObject.fullName = CheckTypeAcceleratorMap(name);

                        if (fullNameObject.fullName == null)
                        {
                            // At this point we don't know for sure if this is a custom type or 
                            // a normal type that is missing the full namespace.
                            // Can we create a full name by checking against our reference map?
                            string referencedFullName = TryReferenceMap(name);
                            if(referencedFullName == null)
                            {
                                fullNameObject.fullName = name;
                                fullNameObject.cannotBeResolved = true;
                            }
                            else
                            {
                                fullNameObject.fullName = referencedFullName;
                            }       
                        }
                        else
                        {
                            fullNameObject.isTypeAccelerator = true;
                        }
                    }
                    fullNameObjectList.Add(fullNameObject);
                }
            }
            // If the CommandAst is a type we don't want to analyze (like a scriptblock),
            // the properties we are trying to access in the above 'if' statement won't exist
            // and will throw an exception.  We'll just catch it and move on since we don't 
            // want to deal with those anyway.
            catch(System.Exception) {}
            return fullNameObjectList;
        }

        ///<summary>
        /// Get full type name from ast object.
        /// For this method's comments let's call .NET and Powershell types 'normal' types.  
        /// Let's call user defined types 'custom' types.
        ///</summary>
        private List<fullTypeNameObject> RetrieveFullTypeName(dynamic typeObject)
        {
            // Check to see if our object is a CommandAst. 
            string AstType = typeObject.GetType().ToString();
            if (AstType.Contains("CommandAst"))
            {
                return GetFullNameFromNewObjectCommand(typeObject);        
            }

            // If we make it here our ast object is either a TypeConstraintAst or a TypeExpressionAst.
            List<fullTypeNameObject> fullNameObjectList = new List<fullTypeNameObject>();

            fullTypeNameObject fullTypeNameInfo = new fullTypeNameObject(false);
            
            var typeNameProperty =  typeObject.TypeName;
            
           
            // Is this a type accelerator? (Most full names can be found this way).
            fullTypeNameInfo.fullName = CheckTypeAcceleratorMap(typeNameProperty.Name);

            if (fullTypeNameInfo.fullName == null)
            {
                // If not a type accelerator, we need to try and find the full name from properties on the ast object.
                // At this point there is no way of knowing if this is a normal type or a custom type.  We will only
                // find out by trying to access different properties on the ast object.
           
                // If a normal type, this property will exist.
                fullTypeNameInfo.fullName = GetAstField(typeNameProperty, "_type");

                if (fullTypeNameInfo.fullName == null)
                {
                    // Check to see if array.
                    if (typeNameProperty.IsArray) 
                    {
                        // normal[]
                        fullTypeNameInfo.fullName = GetAstField(typeNameProperty, "_cachedType");
                        
                        if (fullTypeNameInfo.fullName == null)
                        {
                            fullTypeNameInfo.fullName = typeNameProperty.Name;
        
                            // custom[]
                            if (!StartsWithKnownNamespace(fullTypeNameInfo.fullName))
                            {
                                fullTypeNameInfo.isCustomType = true;
                            }
                        }

                        fullTypeNameInfo.fullName = fullTypeNameInfo.fullName.Replace("[]", "");
                        fullNameObjectList.Add(fullTypeNameInfo);      
                    }

                    // If we've made it here our type is either any combination of normal/custom types in the 
                    // format [[outsideType[insideType]], a singular custom type, OR a type that does not 
                    // exist on the platform executing PSScriptAnalyzer.
                    else        
                    {
                        // SOMETIMES when the case is [normal[normal]], the full type names can only be found
                        // on the typeNameProperty.NonPublic._cachedType property.  Let's check here first.
                        string allFullNames = GetAstField(typeNameProperty, "_cachedType");

                        if (allFullNames != null)
                        {
                            // There may be more than one insideType.
                            string[] splitNames = allFullNames.Split(new Char [] {'[', ',', ']'}, 
                                                           StringSplitOptions.RemoveEmptyEntries);

                            foreach (string name in splitNames)
                            {
                                fullTypeNameObject anotherFullNameObject = new fullTypeNameObject(false);
                                anotherFullNameObject.fullName = name;
                                fullNameObjectList.Add(anotherFullNameObject);
                            }
                        }
                        else 
                        {
                            dynamic typeNameObject;

                            // Try and get outsideType. 
                            try
                            {
                                typeNameObject = typeNameProperty.TypeName;
                            }
                            catch
                            {
                                typeNameObject = null;
                            }
                        
                            if (typeNameObject == null)
                            {
                                //fullTypeNameInfo.fullName = typeNameProperty.FullName;

                                // Can we create a full name by checking against our reference map?
                                fullTypeNameInfo.fullName = TryReferenceMap(typeNameProperty.FullName);

                                if (fullTypeNameInfo.fullName == null)
                                {
                                     fullTypeNameInfo.fullName = typeNameProperty.FullName;

                                    // Is this a normal/known type that the parser couldn't resolve?
                                    if (!StartsWithKnownNamespace(fullTypeNameInfo.fullName) && !PossibleUWPtype(fullTypeNameInfo.fullName))
                                    {
                                        // custom
                                        fullTypeNameInfo.isCustomType = true;
                                    }
                                }

                                fullNameObjectList.Add(fullTypeNameInfo);
                            }

                            else
                            {
                                fullTypeNameObject outsideType = new fullTypeNameObject(false);
                                fullTypeNameObject insideType = new fullTypeNameObject(false);

                                // outsideType is normal.
                                outsideType.fullName = GetAstField(typeNameObject, "_type");
                                
                                if (outsideType.fullName == null)
                                {
                                    outsideType.fullName = GetAstField(typeNameObject, "_name");

                                    // Can we create a full name by checking against our reference map?
                                    if(TryReferenceMap(outsideType.fullName) == null)
                                    {
                                        // outsideType is custom.
                                        outsideType.isCustomType = true;
                                    }                                   
                                }

                                // Sometimes the full names of the outsideTypes will still contain '[]' so 
                                // we need to remove it.
                                string[] split = outsideType.fullName.Split('[');
                                outsideType.fullName = split[0];
                                fullNameObjectList.Add(outsideType);
                                
                                // Get insideType.
                                var typeArguments = typeNameProperty.GenericArguments[0];

                                // insideType is normal.
                                insideType.fullName = GetAstField(typeArguments, "_type");
                        
                                if (insideType.fullName == null)
                                {
                                    insideType.fullName = GetAstField(typeArguments, "_name");

                                    // Can we create a full name by checking against our reference map?
                                    if(TryReferenceMap(insideType.fullName) == null)
                                    {
                                        // insideType is custom.
                                        insideType.isCustomType = true;
                                    } 
                                }
                                fullNameObjectList.Add(insideType);
                            }
                        }
                    }
                }
                else
                {
                    fullNameObjectList.Add(fullTypeNameInfo);
                }
            }
            else
            {
                fullTypeNameInfo.isTypeAccelerator = true;
                fullNameObjectList.Add(fullTypeNameInfo);
            }
        
         return fullNameObjectList;
        }

        /// <summary>
        /// Check if type is present in the target platform type list.
        /// If not, create a Diagnostic Record for that type.
        /// </summary>
        private void CheckCompatibility()
        {
            foreach (dynamic typeObject in allTypesFromAst)
            {
                List<fullTypeNameObject> fullTypeNameObjectList = RetrieveFullTypeName(typeObject);

                foreach (fullTypeNameObject nameObject in fullTypeNameObjectList)
                {
                    int couldNotResolveCount = 0;

                    foreach (dynamic platform in psTypeMap)
                    {   
                        if (nameObject.isCustomType || nameObject.cannotBeResolved)
                        {
                            // If this is a custom type, try and find it in our definition ast list.  If it's there
                            // we can ignore it.
                            if (customTypes.Contains(nameObject.fullName, StringComparer.OrdinalIgnoreCase))
                            {
                                continue;
                            }
                            else
                            {
                                // If this is not a known custom type, it could mean one of two things: 
                                // 1. This is a custom type the user created with Add-Type or with a string
                                //    variable containing the type information.  There is no reasonable way to 
                                //    check for that, so create a diagnostic record.
                                // 2. This is actually a normal type missing the namespace.  We can try and add
                                //    the various known namespaces to the front and look it up that way.  If we find
                                //    it in our library, we can ignore it.  Otherwise we have to warn the user that 
                                //    we could not resolve this type and they should try using the full namespace.
                                if (TryKnownNameSpaces(nameObject.fullName, platform))
                                {
                                    continue;
                                }
                                else
                                {
                                    // If we have multiple target platforms to check and we cannot resolve a type, we don't want
                                    // multiple 'could not resolve' errors for the same type.
                                    if (couldNotResolveCount < 1)
                                    {
                                        GenerateDiagnosticRecord(typeObject, nameObject.fullName, platform.Key, false, nameObject.isTypeAccelerator);
                                    }
                                    couldNotResolveCount++;
                                    
                                }
                            }
                        }
                        else
                        {
                            // Does the target platform library contain this type?           
                            if (platform.Value.Contains(nameObject.fullName))
                            {
                                continue;
                            }
                            else
                            {
                                // If not, then the type is incompatible so generate a Diagnostic Record.
                                GenerateDiagnosticRecord(typeObject, nameObject.fullName, platform.Key, true, nameObject.isTypeAccelerator);
                            } 
                        }
                    }
               }
            }
        }

        /// <summary>
        /// Create an instance of DiagnosticRecord and add to list.
        /// </summary>
        private void GenerateDiagnosticRecord(dynamic astObject, string fullTypeName, string platform, bool resolved, bool typeAccelerator)
        {
            var extent = astObject.Extent;
            var platformInfo = platformSpecMap[platform];

            if (resolved)
            {
                // Here we are just including the type accelerator so the type will be easier to spot in the script
                // on the line number we provide in their diagnostic record.
                string accelerator = "";
                if (typeAccelerator)
                {
                    try
                    {
                        accelerator = " (" + astObject.TypeName?.ToString() + ")";
                    }
                    catch{}
                }
                diagnosticRecords.Add(new DiagnosticRecord(
                    String.Format(
                        Strings.UseCompatibleTypesError,
                        fullTypeName + accelerator,
                        platformInfo.PSEdition,
                        platformInfo.PSVersion,
                        platformInfo.OS),
                    extent,
                    GetName(),
                    GetDiagnosticSeverity(),
                    scriptPath,
                    null,
                    null));
            }
            else
            {
                diagnosticRecords.Add(new DiagnosticRecord(
                    String.Format(
                        Strings.UseCompatibleTypesUnresolvedError,
                        fullTypeName
                        ),
                    extent,
                    GetName(),
                    GetDiagnosticSeverity(),
                    scriptPath,
                    null,
                    null));
            }
        }
    }
}
      
