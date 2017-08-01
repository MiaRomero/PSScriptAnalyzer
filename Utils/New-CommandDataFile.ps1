<#
.SYNOPSIS
    Create a JSON file containing module found in $pshome and their corresponding exported commands

.EXAMPLE
    C:\PS> ./New-CommandDataFile.ps1

    Suppose this file is run on the following version of PowerShell: PSVersion = 6.0.0-aplha, PSEdition = Core, and Windows 10 operating system. Then this script will create a file named core-6.0.0-alpha-windows.json that contains a JSON object of the following form:
    {
        "Modules" : [
            "Module1" : {
                "Name" : "Module1"
                .
                .
                "ExportedCommands" : {...}
            }
            .
            .
            .
        ]
        "JsonVersion" : "0.0.1"
    }

.INPUTS
    None

.OUTPUTS
    None

#>

$jsonVersion = "0.0.1"

$builtinModulePath = Join-Path $pshome 'Modules'
if (-not (Test-Path $builtinModulePath))
{
    throw new "$builtinModulePath does not exist! Cannot create command data file."
}

Function IsPSEditionDesktop
{
    #$edition = Get-Variable -Name PSEdition -ErrorAction Ignore
    #($edition -eq $null) -or ($edition.Value -eq 'Desktop') # $edition is of type psvariable
}

function CheckOS ($osToCheck)
{
    try
    {
        $IsOS = [System.Management.Automation.Platform]::$osToCheck
        return $IsOS
    }
    catch
    {
    }
    return $false
}

Function Get-PowerShellSku
{
    $sku = [ordered]@{
        OS = 'windows'
        PowerShellEdition = $PSEdition.ToString()
        PowerShellVersion = $PSVersionTable.PSVersion.ToString()
    }

    # Get OS
    if (CheckOS IsLinux)
    {
        $sku.OS = 'linux'
    }
    elseif (CheckOS IsOSX)
    {
        $sku.OS = 'osx'
    }
    elseif (CheckOS IsIoT)
    {
        $sku.OS = 'iot'
    }
    elseif (CheckOS IsNanoServer)
    {
        $sku.OS = 'nano'
    }
    # else it is windows, which is already set

    # If desktop edition, check if OS is 32 bit
    if ($sku.PowerShellEdition -eq 'Desktop')
    {
        if (![environment]::Is64BitOperatingSystem) 
        {
            $sku.Architecture = 'x86'
        }
        else 
        {
            $sku.Architecture = 'x64'
        }
    }

    return $sku
}

Function Get-CmdletDataFileName
{
    $PSsku = Get-PowerShellSku
    
    $sb = New-Object 'System.Text.StringBuilder'
    $sb.Append($PSsku.PowerShellEdition) | Out-Null
    $sb.Append('-') | Out-Null
    $sb.Append($PSsku.PowerShellVersion) | Out-Null
    $sb.Append('-') | Out-Null
    $sb.Append($PSsku.OS) | Out-Null
    if ($sku.Architecture)
    {
        $sb.Append('-') | Out-Null
        $sb.Append($PSsku.Architecture) | Out-Null
    }
    $sb.Append('.json') | Out-Null
    $sb.ToString()
}

Function Get-TypesFromAssembliesCORE ()
    {
        $types = @()
        $skippedAssemblies = @()

        Set-Location $pshome

        $allAssemblies = Get-ChildItem *.dll

        foreach ($dll in $allAssemblies)
        {   
            $assembly = "$($dll.BaseName), Culture=neutral"
            $newAssembly=[System.Reflection.AssemblyName]::New($assembly)
            try 
            {
                $loadedAssembly = [System.Reflection.Assembly]::Load($newAssembly) 
                Write-host "Loading assembly: $loadedAssembly"
                $types += $loadedAssembly.GetTypes() | Where-Object { $_.IsPublic } | Select-Object -Property 'Name', 'Namespace'
            }
            catch 
            {
                $skippedAssemblies += $dll
            }
        } 
        return $types
    }

    Function Get-TypeAcceleratorList ()
    {
        $typeAccelerators = @{}

        $typeHash = [psobject].Assembly.GetType("System.Management.Automation.TypeAccelerators")::get
        foreach ($type in $typeHash.GetEnumerator()) { $typeAccelerators.Add( ($type.Key).ToLower(), ($type.Value).fullName ) }

        # IoT and Nano 
        $typeAccelerators.Add("ValidateTrustedData", "System.Management.Automation.ValidateTrustedDataAttribute")

        # Desktop
        $typeAccelerators.Add("adsi", "System.DirectoryServices.DirectoryEntry")
        $typeAccelerators.Add("adsisearcher", "System.DirectoryServices.DirectorySearcher")
        $typeAccelerators.Add("wmiclass", "System.Management.ManagementClass")
        $typeAccelerators.Add("wmi", "System.Management.ManagementObject")
        $typeAccelerators.Add("wmisearcher", "System.Management.ManagementObjectSearcher")

        # special cases
        $typeAccelerators.Add("ordered", "System.Collections.Specialized.OrderedDictionary")
        $typeAccelerators.Add("object", "System.Object")

        # Create json file
        Set-Location F:\ScriptAnalyzer\Libraries\
        $typeAccelerators | ConvertTo-Json | Out-File "typeAccelerators.json" -Encoding utf8 -Force

        return $typeAccelerators
    }   

Function Get-TypesFromAssembliesDesktop
{
    #Set-Location C:\Windows\Microsoft.NET\assembly\GAC_64

}

# Get the modules that will then give us the available cmdlets
$shortModuleInfos = Get-ChildItem -Path $builtinModulePath |
Where-Object {($_ -is [System.IO.DirectoryInfo]) -and (Get-Module $_.Name -ListAvailable)} |
 ForEach-Object {
    $modules = Get-Module $_.Name -ListAvailable
    $modules | ForEach-Object {
        $module = $_
        Write-Progress $module.Name
        $commands = Get-Command -Module $module
        $shortCommands = $commands | Select-Object -Property Name,@{Label='CommandType';Expression={$_.CommandType.ToString()}},ParameterSets
        $shortModuleInfo = $module | Select-Object -Property Name,@{Label='Version';Expression={$_.Version.ToString()}}
        Add-Member -InputObject $shortModuleInfo -NotePropertyName 'ExportedCommands' -NotePropertyValue $shortCommands
        Add-Member -InputObject $shortModuleInfo -NotePropertyName 'ExportedAliases' -NotePropertyValue $module.ExportedAliases.Keys -PassThru
    }
}

# Microsoft.PowerShell.Core is a PSSnapin, hence not handled by the previous code snippet
# get-module -name 'Microsoft.PowerShell.Core' returns null
# whereas get-PSSnapin is not available on PowerShell Core, so we resort to the following
$psCoreSnapinName = 'Microsoft.PowerShell.Core'
Write-Progress $psCoreSnapinName
$commands = Get-Command -Module $psCoreSnapinName
$shortCommands = $commands | Select-Object -Property Name,@{Label='CommandType';Expression={$_.CommandType.ToString()}},ParameterSets
$shortModuleInfo = New-Object -TypeName PSObject -Property @{Name=$psCoreSnapinName; Version=$commands[0].PSSnapin.PSVersion.ToString()}
Add-Member -InputObject $shortModuleInfo -NotePropertyName 'ExportedCommands' -NotePropertyValue $shortCommands

# Find the exported aliases for the commands in Microsoft.PowerShell.Core
$aliases = Get-Alias * | Where-Object {($commands).Name -contains $_.ResolvedCommandName}
if ($null -eq $aliases)
{
    $aliases = @()
}
else 
{
    $aliases = $aliases.Name
}

Add-Member -InputObject $shortModuleInfo -NotePropertyName 'ExportedAliases' -NotePropertyValue $aliases

# Combine cmdlet info from modules and Microsoft.PowerShell.Core into one list
$allShortModuleInfos = $shortModuleInfos + $shortModuleInfo

#########################TYPES##########################

# Get publicly available types
$PSsku = Get-PowerShellSku
$OS = $PSsku.OS
$Edition = $PSsku.PowerShellEdition
$typeList = @()
$typeAccelerators = @{}

if ($OS -eq 'windows' -and $Edition -eq 'Core' )
{
    $typeList = Get-TypesFromAssembliesCORE
    $typeAccelerators = Get-TypeAcceleratorList
}

# Need instruction on how to get from VM
if ($OS -eq 'linux')
{
    $typeList = Get-TypesFromAssembliesCORE
    $typeAccelerators = Get-TypeAcceleratorList
}

# Need instruction on how to get from VM
if ($OS -eq 'osx')
{
    $typeList = Get-TypesFromAssembliesCORE
    $typeAccelerators = Get-TypeAcceleratorList
}

# [AppDomain] is not available on IoT or NanoServer, so must access assemblies and types another way
if ($OS -eq 'iot')
{
    # Need to get and load PowerShell assemblies in C:\Windows\System32\WindowsPowerShell\v1.0
    Set-Location C:\Windows\System32\WindowsPowerShell\v1.0
    $powerShellDlls = Get-ChildItem -Filter *.dll

    # List of assemblies in this directory which we do not need/don't contain public types
    $doNotUsePowerShell = @("Microsoft.Management.Infrastructure.Native.Unmanaged.dll", "PSEvents.dll", "pwrshsip.dll")

    # For each assembly in our list, we will load it and get its public types
    $typeList += Get-TypesFromAssemblies $powerShellDlls $doNotUsePowerShell

    # We also need dependency assemblies from C:\windows\system32\DotNetCore\v1.0
    Set-Location C:\windows\system32\DotNetCore\v1.0
    $dependencyDlls = Get-ChildItem -Filter *.dll

    # List of assemblies in this directory which we do not need/don't contain public types
    $doNotUseDotNet = @("clrcompression.dll", "clretwrc.dll", "clrjit.dll", "coreclr.dll", "mscorlib.ni.dll", "mscorrc.debug.dll", "mscorrc.dll", "System.Private.CoreLib.ni.dll")

    # For each assembly in our list, we will load it and get its public types
    $typeList += Get-TypesFromAssemblies $dependencyDlls $doNotUseDotNet
}
elseif ($OS -eq 'nano')
{

    # create json object and create file like normal
    # exit vm session 
    # copy json file from session to local destination  >cp -FromSession $nano -Path c:\Core-5.1.16221.1000-nano.json -Destination .
    # create PS object from json file >$nanoJson = (Get-Content .\Core-5.1.16221.1000-nano.json) -join "`n" | ConvertFrom-Json
    # run script below

    # Navigate to type catalog location (on windows Core)
    # $path = "F:\GitHub\PowerShell\src\Microsoft.PowerShell.CoreCLR.AssemblyLoadContext\CorePsTypeCatalog.cs"

    Set-Location $pshome\..\..\..\..\..\..\Microsoft.PowerShell.CoreCLR.AssemblyLoadContext
    $path  = Join-Path -Path (Convert-Path .) -ChildPath "CorePsTypeCatalog.cs"

    $typeList = @()

    $lines = Get-Content $path

    foreach ($line in $lines)
    {
        if( $line.Contains("typeCatalog["))
        {
            $newType = @{}

            $line = $line.Split("=")[0]
            $line = $line.Replace('typeCatalog["', "").Replace('"]', "").Trim()
            $parts = $line.Split(".")
            $name = $parts[$parts.Count -1]
            $newType['Name'] = $name
            $nameSpace = ''
            for ($i = 0; $i -lt ($parts.Count -1); $i++) {
                $nameSpace += $parts[$i]
                if ($i -lt ($parts.Count - 2))
                {
                    $nameSpace += '.'
                }
            }
            $newType['Namespace'] = $nameSpace

            $typeList += $newType
        }
    }

    # add typeList to nanoJson PS object >$nanoJson.Types += $typeList
    # convert PS object back to json file >$nanoJson | ConvertTo-Json -Depth 4 | Out-File .\Core-5.1.16221.1000-nano.json -Encoding utf8 -Force
}

# Create our json object and json file
$jsonData = [ordered]@{
    Edition = Get-PowerShellSku
    Modules = $allShortModuleInfos
    Types = $typeList
    SchemaVersion = $jsonVersion
}
Set-Location C:\
$jsonData | ConvertTo-Json -Depth 4 | Out-File ((Get-CmdletDataFileName)) -Encoding utf8

<# close nanoVM session rsn
cd F:\Git\os\src\onecore\admin\monad\nttargets\bin\PsOnCssScripts\OneCorePSDev\
net use \\10.123.171.171\c$ <IP of nanoVM>
dir \\10.123.171.171\c$
Import-Module .\OneCorePSDev.psm1 -Force
Update-OneCorePowerShell -BinaryFolder \\winbuilds\release\RS_SRVCOMMON_PS\16221.1000.170612-1700\amd64fre\bin\ -CssShare \\10.123.171.171\c$  <latest build, nanoVM IP>
New-PSSession -ComputerName 10.123.171.171 -Credential Administrator <IP of nanoVM>
etsn
cd $PSHOME
all the dll's should now be in this directory
dotnetcore dll's should already be there#>


<#
$folders = Get-ChildItem -Recurse -Filter *.dll | ForEach-Object {Split-Path $_.FullName -Parent} | Select-Object -Unique
$folders | % { pushd $_; Write-Host $_ -ForegroundColor Green;dir ; popd }

$count = 0
$types = @()

$folders | % { pushd $_; Write-Host $_ -ForegroundColor Green; $dll = Get-ChildItem -Filter *.dll | Foreach-Object { Write-Host $_.BaseName -ForegroundColor Red ; 
    Write-host "Loading assembly: $($_.BaseName)"; $count++; $myAssembly = [System.Reflection.Assembly]::LoadFile($_.FullName); 
    $types+= $myAssembly.GetTypes() | Where-Object {$_.IsPublic}} ; popd }



try
{
    $myAssembly = [System.Reflection.Assembly]::LoadFile('C:\windows\Microsoft.NET\assembly\GAC_64\Microsoft.SqlServer.XEvent.Linq\v4.0_12.0.0.0__89845dcd8080cc91.Microsoft.SqlServer.XEvent.Linq.dll'); 
    $types+= $myAssembly.GetTypes() | Where-Object {$_.IsPublic}
}
catch 
{
   ex
}

$types = @()
$assembly = ""
$failed = @()

    foreach ($dll in $powerShellDlls)
    {   
        try 
        {
            $assembly = $dll.FullName
            Write-host "Loading assembly: $assembly"
            $loadedAssembly =[System.Reflection.Assembly]::LoadFile($assembly)
            #$loadedAssembly = [System.Reflection.Assembly]::Load($newAssembly) 
            Write-Host "___________________________________"
            $types += $loadedAssembly.GetTypes() | Where-Object { $_.IsPublic } | Select-Object -Property 'Name', 'Namespace'
        } 
        catch {
            Write-Host "Failed:  $assembly"   -ForegroundColor Red
            $failed += $dll.Name
        }
    }

    [environment]::Is64BitOperatingSystem  #(false on x86) OS

    [IntPtr]::Size -eq 4 #(true on x86, 32 bit) version of PS 

    

    Function Get-TypesFromAssemblies ($dllList, $doNotUseList)
{
    $types = @()
    foreach ($dll in $coreCLR | Where-Object { $doNotUseList -notcontains $_.Name} )
    {   
        $assembly = "$($dll.BaseName), Culture=neutral"
        $newAssembly=[System.Reflection.AssemblyName]::New($assembly)
        $loadedAssembly = [System.Reflection.Assembly]::Load($newAssembly) 
        Write-host "Loading assembly: $loadedAssembly"
        $types += $loadedAssembly.GetTypes() | Where-Object { $_.IsPublic } | Select-Object -Property 'Name', 'Namespace'
    } 
    return $types
}
#>
    #################################################################################################################################
    
    
    
    

    <#For windowsCore (PowerShell 6.0.0-beta)
    $pshome (F:\GitHub\PowerShell\src\powershell-win-core\bin\Debug\netcoreapp2.0\win10-x64\publish)

    // PowerShell assemblies
    $psAssemblies = @(dir Microsoft.PowerShell*, Microsoft.WSMan*, Microsoft.Management.Infrastructure*, System.Management.Automation* -Include *.dll | % name)

    // Core assemblies
    $coreCLR = dir *.dll | ? { $psAssemblies -notcontains $_.name }

    $types = @()
    foreach ($dll in $coreCLR)
    {   
        $assembly = "$($dll.BaseName), Culture=neutral"
        $newAssembly=[System.Reflection.AssemblyName]::New($assembly)
        $loadedAssembly = [System.Reflection.Assembly]::Load($newAssembly) // There will be errors for the dll's that don't load (we don't need those anyway)
        Write-host "Loading assembly: $dll"
        $types += $loadedAssembly.GetTypes() | Where-Object { $_.IsPublic } | Select-Object -Property 'Name', 'Namespace'
    } 
    foreach ($dll in $psAssemblies)
    {   
        $assembly = "$($dll.BaseName), Culture=neutral"
        $newAssembly=[System.Reflection.AssemblyName]::New($assembly)
        $loadedAssembly = [System.Reflection.Assembly]::Load($newAssembly) 
        Write-host "Loading assembly: $dll"
        $types += $loadedAssembly.GetTypes() | Where-Object { $_.IsPublic } | Select-Object -Property 'Name', 'Namespace'
    } 

    $typeAccelerators = @{}
    $typeHash = [psobject].Assembly.GetType("System.Management.Automation.TypeAccelerators")::get
    foreach ($type in $typeHash.GetEnumerator()) { $typeAccelerators.Add( $type.Key, ($type.Value).fullName ) }


        
    #>

    <#
        The type accelerator (TA) list is built from PowerShell Core.  The TA list on Linux is the same; IoT
        and Nano have 1 additional; Full PowerShell has 5 additional; and 'ordered' is not a true TA, but acts like
        one so it needs to be added.
    #>
    


    