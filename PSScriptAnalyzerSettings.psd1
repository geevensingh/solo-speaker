@{
    Severity     = @('Error', 'Warning')

    ExcludeRules = @(
        # The install and uninstall scripts are interactive, human-facing console tools.
        # Their output is instructions to the person running them -- where the binary
        # went, whether --restore succeeded, and what to check if it did not. Write-Output
        # would put that text on the pipeline, and Write-Information is invisible by
        # default, which is the wrong failure mode for a message whose whole purpose is to
        # tell you that a machine may still be muted.
        'PSAvoidUsingWriteHost'
    )
}
