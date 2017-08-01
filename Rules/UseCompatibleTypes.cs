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
        // List of user created types found in ast (TypeDefinitionAst).
        private List<string> customTypes;
        private bool IsInitialized;
        private bool hasInitializationError;

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
            return RuleSeverity.Warning;
        }

        /// <summary>
        /// Gets the severity of the returned diagnostic record: error, warning, or information.
        /// </summary>
        public DiagnosticSeverity GetDiagnosticSeverity()
        {
            return DiagnosticSeverity.Warning;
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
        /// <param name="ast">AST to be analyzed. This should be non-null</param>
        /// <param name="fileName">Name of file that corresponds to the input AST.</param>
        /// <returns>An enumerable type containing the violations</returns>
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

            // These are types named with the 'New-Object' cmdlet.
            IEnumerable<Ast> commandAsts = ast.FindAll(testAst => testAst is CommandAst, true);
            addAstElementToList(GetNewObjectAsts(commandAsts)); 

            // These are user-created types.
            IEnumerable<Ast> definitionAsts = ast.FindAll(testAst => testAst is TypeDefinitionAst, true);
            foreach (Ast item in definitionAsts)
            {
                string customType = item.GetType().GetProperty("Name").GetValue(item).ToString();
                customTypes.Add(customType);
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
        ///  Get only the 'New-Object' CommandAsts.
        /// </summary>
        private List<Ast> GetNewObjectAsts(dynamic commandAsts)
        {
            List<Ast> newObjectAsts = new List<Ast>();
            foreach (dynamic item in commandAsts)
            {   
                try
                {
                    // Get only the 'New-Object' command asts that are NOT creating a COM object.
                    if (String.Equals(item.CommandElements[0].Value.ToString(),
                                    "New-Object", 
                                    StringComparison.OrdinalIgnoreCase) 
                                    && 
                                    !(item.CommandElements[1].ParameterName.ToString()).
                                    StartsWith("com", StringComparison.OrdinalIgnoreCase)
                                    )
                    {
                        newObjectAsts.Add(item);
                    }
                }
                // If the CommandAst is a type we don't want to analyze (like a scriptblock),
                // the properties we are trying to access in the above 'if' statement won't exist
                //  and will throw an exception.  We'll just catch it and move on since we don't 
                // want to deal with those anyway.
                catch(System.Exception) {}
            }
            return newObjectAsts;
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
        /// Check if rule arguments are valid (at least one argument must be 'compatibility').
        /// </summary>
        private bool RuleParamsValid(Dictionary<string, object> ruleArgs)
        {
            return ruleArgs.Keys.Any(key => validParameters.Equals(key, StringComparison.OrdinalIgnoreCase));
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
        /// Get a hashset of full type names from a deserialized json file.
        /// </summary>
        private HashSet<string> GetTypesFromData(dynamic deserializedObject)
        {
            var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            dynamic typeList = deserializedObject.Types;
            foreach (dynamic type in typeList)
            {   
                var name = type.Name.ToObject<string>();
                var nameSpace = type.Namespace.ToObject<string>();       
                var fullName = nameSpace + "." + name;
                types.Add(fullName);
            }           
            return types;
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
        /// Sets up dictionaries indexed by PowerShell version/edition and OS, and
        /// type accelerator dictionary.
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

            // Get path where the json libraries are located.
            string settingsPath = Settings.GetShippedSettingsDirectory();

            if (settingsPath == null)
            {
                return;
            }

            // Find corresponding libraries for target platforms and create type maps.
            ProcessDirectory(settingsPath, compatibilityList);
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
            psTypeMap = new Dictionary<string, HashSet<string>>();
            typeAcceleratorMap = new Dictionary<string, string>();
            platformSpecMap = new Dictionary<string, dynamic>(StringComparer.OrdinalIgnoreCase);
            SetupTypesDictionary();
            IsInitialized = true;
        }

        /// <summary>
        /// Check if type is present in the target platform type list.
        /// If not, create a Diagnostic Record for that type.
        /// </summary>
        private void CheckCompatibility()
        {
            foreach (dynamic typeObject in allTypesFromAst)
            {
               List<string> fullTypeNames =  RetrieveFullTypeName(typeObject);
               foreach (string typeName in fullTypeNames)
               {
                    foreach (var platform in psTypeMap)
                    {    
                        // Does the target platform library contain this type?           
                        if (platform.Value.Contains(typeName, StringComparer.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        else
                        {
                            // Check to see if the type is a custom type the user has created.  
                            // If not, then the type is incompatible so generate a Diagnostic Record.
                            if (customTypes.Contains(typeName, StringComparer.OrdinalIgnoreCase))
                            {
                                continue;
                            }
                            else
                            {
                                GenerateDiagnosticRecord(typeObject, typeName, platform.Key);
                            }
                        }
                    }
               }
            }
        }

        ///<summary>
        /// Check type Accelerator map for full type name.
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
        /// Retrieve full type name from a 'New-Object' CommandAst.
        ///</summary>
        private List<string> GetFullNameFromNewObjectCommand(dynamic typeObject)
        {
            List<string> fullNames = new List<string>();
            string fullName = null;

            dynamic astElement = typeObject.CommandElements[1];

            // Is the first ast element after 'New-Object' a parameter?  If yes, we want
            // the next element.
            string paramName = astElement.GetType().ToString();
            if (paramName.Contains("CommandParameterAst"))
            {
                astElement = typeObject.CommandElements[2];
            }
            
            string commandElementName = astElement.Value.ToString();

            // Possibility typeName could contain an array.
            string [] typeNameElements = commandElementName.Split(new Char [] {'[', ',', ']'}, 
                                                            StringSplitOptions.RemoveEmptyEntries);
            
            foreach (string typeName in typeNameElements)
            {
                // Is typeName already a full name i.e. begins with 'System.'?
                if (typeName.StartsWith("System.", StringComparison.OrdinalIgnoreCase))
                {
                    fullName = typeName;
                }
                else
                {
                    // Not a full name so check if a type accelerator.
                    fullName = CheckTypeAcceleratorMap(typeName);

                    // Not a type accelerator so check if it is a custom type.
                    if (fullName == null)
                    {
                        if (customTypes.Contains(typeName, StringComparer.OrdinalIgnoreCase))
                        {
                            fullName = typeName;
                        }
                        // Not a custom type so manually make fullname.
                        else
                        {
                        //fullName = "System." + typeName; 
                        fullName = typeName;
                        }
                    }
                }
                fullNames.Add(fullName);
            }   
            return fullNames;
        }

        ///<summary>
        /// Retrieve non-public property from ast object.
        ///</summary>
        private string GetAstField (dynamic typeNamePropertyObject, string desiredProperty)
        {
            return typeNamePropertyObject.GetType().GetField(
            desiredProperty, BindingFlags.Instance|BindingFlags.NonPublic)?.GetValue(
            typeNamePropertyObject)?.ToString();
        }

        ///<summary>
        /// Get full type name from ast object.
        ///
        /// Let's call .NET and Powershell types 'normal' types.  Let's call user defined types 'custom' types.
        /// The following cases are possible (shown with example):
        ///     normal              [String]
        ///     custom              [MyType]
        ///     normal[]            [String[]]
        ///     custom[]            [MyType[]]
        ///     normal[normal]      [List[String]]
        ///     normal[custom]      [List[MyType]]
        ///     custom[custom]      [MyList[MyType]]
        ///     custom[normal]      [MyList[String]]
        ///</summary>
        private List<string> RetrieveFullTypeName(dynamic typeObject)
        {
            // Check to see if this a CommandAst. 
            string AstType = typeObject.GetType().ToString();
            if (AstType.Contains("CommandAst"))
            {
                try
                { 
                    return GetFullNameFromNewObjectCommand(typeObject);        
                }
                catch (System.Exception) {}
            }

            // If we make it here our ast object is either a TypeConstraintAst or a TypeExpressionAst.
            List<string> fullNames = new List<string>();
            string fullName = null;
            var typeNameProperty =  typeObject.TypeName;

            // Is this a type accelerator? (Most full names can be found this way).
            fullName = CheckTypeAcceleratorMap(typeNameProperty.Name);
            if (fullName != null)
            {
                fullNames.Add(fullName);
            }
            
            // If not a type accelerator, we need to try and find the full name from properties on the ast object.
            else
            {
                // normal
                fullName = GetAstField(typeNameProperty, "_type");

                if(fullName != null)
                {
                    fullNames.Add(fullName);
                }
            
                if (fullName == null)
                {
                    // Check to see if array.
                    if (typeNameProperty.IsArray) 
                    {
                        // normal[]
                        fullName = GetAstField(typeNameProperty, "_cachedType");
                        
                        if (fullName == null)
                        {
                            // custom[]
                            fullName = GetAstField(typeNameProperty, "_cachedFullName");
                        }
                        
                        fullName = fullName.Replace("[]", "");
                        fullNames.Add(fullName);                  
                    }

                    // Is either any combination of normal/custom types in the format [[outsideType[insideType]],
                    // OR a singular custom.
                    else        
                    {
                        // SOMETIMES when the case is [normal[normal]], the full type names can only be found
                        // on the typeNameProperty.NonPublic._cachedType property.  Let's check here first.
                        // If this is not a [normal[normal]] case, the aforementioned property will not exist.
                        string allFullNames = GetAstField(typeNameProperty, "_cachedType");

                        if (allFullNames != null)
                        {
                            // There may be more than one insideType.
                            string[] splitNames = allFullNames.Split(new Char [] {'[', ',', ']'}, 
                                                           StringSplitOptions.RemoveEmptyEntries);

                            foreach (string name in splitNames)
                            {
                                fullNames.Add(name);
                            }
                        }
                        else 
                        {
                            dynamic typeNameObject;

                            // Try and get outsideType. If it's null then our object is singular custom.
                            try
                            {
                                typeNameObject = typeNameProperty.TypeName;
                            }
                            catch (System.Exception) // if typeNameProperty.TypeName property doesn't exist.
                            {
                                typeNameObject = null;
                            }
                        
                            if (typeNameObject == null)
                            {
                                // custom
                                fullName = typeNameProperty.FullName;
                                fullNames.Add(fullName);
                            }

                            else
                            {
                                string outside = null;
                                string inside = null;

                                // outsideType is normal
                                outside = GetAstField(typeNameProperty, "_type");
                                
                                if (outside == null)
                                {
                                    // outsideType is custom
                                    outside = GetAstField(typeNameProperty, "_name");
                                }

                                // Sometimes the full names of the outsideTypes will still contain '[]' so 
                                // we need to remove it.
                                string[] split = outside.Split('[');
                                outside = split[0];
                                fullNames.Add(outside);
                                
                                // Get insideType.
                                var typeArguments = typeNameProperty.GenericArguments[0];

                                // insideType is normal
                                inside = GetAstField(typeNameProperty, "_type");
                        
                                if (inside == null)
                                {
                                    // insideType is custom
                                    inside = GetAstField(typeNameProperty, "_name");
                                }
                                fullNames.Add(inside);
                            }
                        }
                    }
                }
            }
         return fullNames;
        }

        /// <summary>
        /// Create an instance of DiagnosticRecord and add to list.
        /// </summary>
        private void GenerateDiagnosticRecord(dynamic astObject, string fullTypeName, string platform)
        {
            var extent = astObject.Extent;
            var platformInfo = platformSpecMap[platform];
    
            diagnosticRecords.Add(new DiagnosticRecord(
                String.Format(
                    Strings.UseCompatibleTypesError,
                    fullTypeName,
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
    }
}
      
