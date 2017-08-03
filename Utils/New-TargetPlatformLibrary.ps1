<#
.SYNOPSIS
    Create a JSON file containing modules found in $pshome and their corresponding exported commands, and available types.

.EXAMPLE
    C:\PS> ./New-CommandDataFile.ps1

    Suppose this file is run on the following version of PowerShell: PSVersion = 6.0.0-aplha, PSEdition = Core, and Windows 10 operating system.
    Then this script will create a file named core-6.0.0-alpha-windows.json that contains a JSON object of the following form:
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
        ],
        "Types" : [
            {
                "Name":
                "Namespace":
            },
            .
            .
            .
        ],
        "SchemaVersion" : "0.0.1"
    }

.INPUTS
    None

.OUTPUTS
    None

#>

$jsonVersion = "0.0.1"



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
        PowerShellEdition = $PSEdition.ToString().ToLower()
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

    return $sku
}

Function Get-CmdletDataFileName ($PSsku)
{
    #$PSsku = Get-PowerShellSku
    
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


    [scriptblock]$retrieveCmdletScript = {

        $builtinModulePath = Join-Path $pshome 'Modules'
        if (-not (Test-Path $builtinModulePath))
        {
            throw new "$builtinModulePath does not exist! Cannot create command data file."
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

        return $allShortModuleInfos
    }

    [scriptblock]$retrieveTypesScript = {

        param($paths)

        $types = @()
        $skippedAssemblies = @()

        foreach($path in $paths)
        {
            Set-Location $path
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
        }

        
        return $types
    }

<#
$retrieveCmdletScript = {

    $builtinModulePath = Join-Path $pshome 'Modules'
    if (-not (Test-Path $builtinModulePath))
    {
        throw new "$builtinModulePath does not exist! Cannot create command data file."
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

    return $allShortModuleInfos
}

$retrieveTypesScript = {

    Set-Location $pshome

    $types = @()
    $skippedAssemblies = @()

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
}#>

$createTypeAcceleratorFileScript = {

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
    Set-Location F:\ScriptAnalyzer\Libraries\Test
    $typeAccelerators | ConvertTo-Json | Out-File "typeAccelerators.json" -Encoding utf8 -Force
}


############# Windows Core ############
$windowsSku = [ordered]@{
    OS = 'windows'
    PowerShellEdition = $PSEdition.ToString().ToLower()
    PowerShellVersion = $PSVersionTable.PSVersion.ToString()
}

Set-Location $pshome

$windowsJsonData = [ordered]@{}

$windowsJsonData.Edition = $windowsSku
$windowsJsonData.Modules = & $retrieveCmdletScript
$windowsJsonData.Types = & { $retrieveTypesScript } -ArgumentList @($pshome)
$windowsJsonData.SchemaVersion = $jsonVersion

Set-Location F:\ScriptAnalyzer\Libraries\Test
$windowsJsonData | ConvertTo-Json -Depth 4 | Out-File ((Get-CmdletDataFileName($windowsSku))) -Encoding utf8 -Force

& $createTypeAcceleratorFileScript

############## IoT ####################

$ip = "10.123.171.190"
$user = "Administrator"
$password = ConvertTo-SecureString -String "p@ssw0rd" -asplaintext -force
$credentials = New-Object -TypeName System.Management.Automation.PSCredential -argumentlist $user, $password
$s = New-PSSession -ComputerName $ip -Credential $credentials

$PSInfo = Invoke-Command -Session $s -ScriptBlock {
            $o = [PSObject]@{
                PSVersion = $PSVersionTable.PSVersion
                PSEdition = $PSEdition
            }
            return $o
          }

$IoTSku = [ordered]@{
    OS = 'iot'
    PowerShellEdition = $PSInfo.PSEdition.ToString().ToLower()
    PowerShellVersion = $PSInfo.PSVersion.ToString()
}

$typePaths = @($pshome, "C:\windows\system32\DotNetCore\v1.0")

$IoTJsonData = [ordered]@{}

$IoTJsonData.Edition = $IoTSku

$IoTJsonData.Modules = (Invoke-Command -Session $s -ScriptBlock { 
                        param([string]$getCmdlets)
                        $sb = [scriptblock]::Create($getCmdlets)
                        [psobject]@{ output = &$sb }
                        } -ArgumentList $retrieveCmdletScript).output

$IoTJsonData.Types = (Invoke-Command -Session $s -ScriptBlock { 
                        param([string]$getTypes, [string[]]$typePaths)
                        $sb = [scriptblock]::Create($getTypes)
                        [psobject]@{ output = &$sb -Path $typePaths}
                        } -ArgumentList $retrieveTypesScript, $typePaths).output

$IoTJsonData.SchemaVersion = $jsonVersion

Set-Location F:\ScriptAnalyzer\Libraries\Test
$IoTJsonData | ConvertTo-Json -Depth 4 | Out-File ((Get-CmdletDataFileName($IoTSku))) -Encoding utf8 -Force

Remove-PSSession $s

<#$parts = @(
    @{
        Modules = $retrieveCmdletScript
    },
    @{
        Types = $retrieveTypesScript
    }
)



foreach ($key in $parts)
{
    $IoTJsonData.$key = (Invoke-Command -Session $s -ScriptBlock { 
                        param([string]$command)
                        $sb = [scriptblock]::Create($command)
                        [psobject]@{ output = &$sb }
                        } -ArgumentList $parts[$key]).output
}#>

############# Linux ##################

$linuxScript = ".{

    $retrieveCmdletScript = {

        $builtinModulePath = Join-Path $pshome 'Modules'
        if (-not (Test-Path $builtinModulePath))
        {
            throw new '$builtinModulePath does not exist! Cannot create command data file.'
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

        return $allShortModuleInfos
    }

    $retrieveTypesScript = {

        Set-Location $pshome

        $types = @()
        $skippedAssemblies = @()

        $allAssemblies = Get-ChildItem *.dll

        foreach ($dll in $allAssemblies)
        {   
            $assembly = '$($dll.BaseName), Culture=neutral'
            $newAssembly=[System.Reflection.AssemblyName]::New($assembly)
            try 
            {
                $loadedAssembly = [System.Reflection.Assembly]::Load($newAssembly) 
                Write-host 'Loading assembly: $loadedAssembly'
                $types += $loadedAssembly.GetTypes() | Where-Object { $_.IsPublic } | Select-Object -Property 'Name', 'Namespace'
            }
            catch 
            {
                $skippedAssemblies += $dll
            }
        }
        return $types
    }

    $linuxSku = [ordered]@{
        OS = 'linux'
        PowerShellEdition = $PSEdition.ToString().ToLower()
        PowerShellVersion = $PSVersionTable.PSVersion.ToString()
    }

    $linuxJsonData = [ordered]@{}

    $linuxJsonData.Edition = $linuxSku
    $linuxJsonData.Modules = & $retrieveCmdletScript
    $linuxJsonData.Types = & $retrieveTypesScript
    $linuxJsonData.SchemaVersion = $jsonVersion

    $windowsJsonData | ConvertTo-Json
}"

$bytes = [text.encoding]::Unicode.GetBytes($linuxScript)
$encodedCommand = [convert]::ToBase64String($bytes)

$jsonData = ssh mromero@10.123.170.132 powershell -encodedcommand "$encodedCommand"

$linuxSku = $jsonData.Edition 

$jsonData | Out-File ((Get-CmdletDataFileName($linuxSku))) -Encoding utf8 -Force