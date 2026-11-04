# Sourced by bash/zsh, after the user's prompt setup.
if [ "${AGWINTERM_ENABLED:-}" = 1 ] && [ -z "${__agw_si:-}" ]; then
    __agw_si=1
    # Only the outer pane shell may end its resume binding. Tool shells inherit
    # this owner, so their prompts cannot unbind an agent that is still running.
    export AGWINTERM_PROMPT_OWNER="${AGWINTERM_PROMPT_OWNER:-linux:$$}"
    __agw_prompt_seen=
    # The shared Codex daemon retains its first launcher's environment, so its
    # hooks cannot reliably address this pane. Keep terminal launches local.
    # Preserve user aliases/functions; those can opt in with --no-daemon themselves.
    if ! alias codex >/dev/null 2>&1; then
    case "$(command -v codex 2>/dev/null)" in
        /*)
            function codex {
                if [ "${AGWINTERM_ENABLED:-}" = 1 ]; then
                    command codex --no-daemon "$@"
                else
                    command codex "$@"
                fi
            }
            ;;
    esac
    fi
    __agw_prompt() {
        if [ -n "$__agw_prompt_seen" ]; then
            if [ "$AGWINTERM_PROMPT_OWNER" = "linux:$$" ]; then
                printf '\033]777;agwinterm;command-finished\007'
            fi
        else
            __agw_prompt_seen=1
        fi
        # Percent-encode bytes so spaces, %, Unicode and control characters cannot corrupt OSC 7.
        local remaining="$PWD" out='' c
        local LC_ALL=C
        while [ -n "$remaining" ]; do
            c="${remaining%"${remaining#?}"}"
            remaining="${remaining#?}"
            case "$c" in
                [a-zA-Z0-9/._~-]) out="$out$c" ;;
                *) printf -v c '%%%02X' "'$c"; out="$out$c" ;;
            esac
        done
        printf '\033]7;file://localhost%s\007' "$out"
    }
    if [ -n "${ZSH_VERSION:-}" ]; then
        autoload -Uz add-zsh-hook
        add-zsh-hook precmd __agw_prompt
    elif [ -n "${BASH_VERSION:-}" ]; then
        if [[ "$(declare -p PROMPT_COMMAND 2>/dev/null)" == 'declare -a '* ]]; then
            PROMPT_COMMAND+=(__agw_prompt)
        else
            PROMPT_COMMAND="${PROMPT_COMMAND:+$PROMPT_COMMAND$'\n'}__agw_prompt"
        fi
    fi
fi
