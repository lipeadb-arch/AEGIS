@{
    RootModule        = 'MicrosoftTeams.psm1'
    ModuleVersion     = '0.0.1'
    GUID              = 'b6f2a3c1-0d4e-4f5a-9b8c-7d6e5f4a3b21'
    Author            = 'AEGIS (fixture de teste)'
    Description       = 'Fixture de teste do AEGIS: NÃO é o módulo oficial do Microsoft Teams.'
    PowerShellVersion = '7.2'
    FunctionsToExport = @('Connect-MicrosoftTeams', 'Disconnect-MicrosoftTeams', 'Get-CsTeamsClientConfiguration',
        'Get-CsTenantFederationConfiguration', 'Get-CsTeamsMeetingPolicy', 'Get-CsTeamsMessagingPolicy', 'Get-CsTeamsAppPermissionPolicy',
        'Get-CsGroupPolicyAssignment')
    CmdletsToExport   = @()
    AliasesToExport   = @()
}
