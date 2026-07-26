# Bash completion for Morsa 1.0. Source this file or install it under
# /usr/share/bash-completion/completions/morsa.
_morsa_complete()
{
    local current previous command subcommand
    COMPREPLY=()
    current="${COMP_WORDS[COMP_CWORD]}"
    previous="${COMP_WORDS[COMP_CWORD-1]}"
    command="${COMP_WORDS[1]:-}"
    subcommand="${COMP_WORDS[2]:-}"

    case "${previous}" in
        --project|--output|--rules|--payload)
            COMPREPLY=( $(compgen -f -- "${current}") )
            return ;;
        --proxy-pool|--pool)
            # Pool names are workspace state; avoid executing Morsa during completion.
            return ;;
        --policy)
            COMPREPLY=( $(compgen -W 'sticky round-robin random weighted least-latency failover' -- "${current}") )
            return ;;
        --format)
            COMPREPLY=( $(compgen -W 'json html csv graphml gexf dot' -- "${current}") )
            return ;;
        --max-mode)
            COMPREPLY=( $(compgen -W 'passive active aggressive' -- "${current}") )
            return ;;
        --kind)
            COMPREPLY=( $(compgen -W 'domain host url cidr ip' -- "${current}") )
            return ;;
    esac

    if [[ ${COMP_CWORD} -eq 1 ]]; then
        COMPREPLY=( $(compgen -W 'init doctor version project scope ingest discover fetch provider run analyze correlate recon fingerprint web malware graph plugin proxy report help' -- "${current}") )
        return
    fi

    case "${command}" in
        project) COMPREPLY=( $(compgen -W 'status' -- "${current}") ) ;;
        scope) COMPREPLY=( $(compgen -W 'add list' -- "${current}") ) ;;
        ingest) COMPREPLY=( $(compgen -W 'file directory url' -- "${current}") ) ;;
        discover) COMPREPLY=( $(compgen -W 'documents history import' -- "${current}") ) ;;
        fetch) COMPREPLY=( $(compgen -W 'pending url' -- "${current}") ) ;;
        provider) COMPREPLY=( $(compgen -W 'list status bootstrap' -- "${current}") ) ;;
        run) COMPREPLY=( $(compgen -W 'full resume' -- "${current}") ) ;;
        analyze) COMPREPLY=( $(compgen -W 'all' -- "${current}") ) ;;
        recon) COMPREPLY=( $(compgen -W 'dns reverse subdomains range axfr' -- "${current}") ) ;;
        fingerprint) COMPREPLY=( $(compgen -W 'http tls banner' -- "${current}") ) ;;
        web) COMPREPLY=( $(compgen -W 'crawl backups' -- "${current}") ) ;;
        malware) COMPREPLY=( $(compgen -W 'scan yara' -- "${current}") ) ;;
        graph) COMPREPLY=( $(compgen -W 'export' -- "${current}") ) ;;
        plugin) COMPREPLY=( $(compgen -W 'list inspect install update activate rollback remove run' -- "${current}") ) ;;
        proxy)
            if [[ ${COMP_CWORD} -eq 2 ]]; then
                COMPREPLY=( $(compgen -W 'pool source import status reset test' -- "${current}") )
            elif [[ "${subcommand}" == 'pool' && ${COMP_CWORD} -eq 3 ]]; then
                COMPREPLY=( $(compgen -W 'add list' -- "${current}") )
            elif [[ "${subcommand}" == 'source' && ${COMP_CWORD} -eq 3 ]]; then
                COMPREPLY=( $(compgen -W 'list load' -- "${current}") )
            fi ;;
        report) COMPREPLY=( $(compgen -W 'json html csv bundle' -- "${current}") ) ;;
        *)
            COMPREPLY=( $(compgen -W '--project --json --help' -- "${current}") ) ;;
    esac
}
complete -F _morsa_complete morsa
