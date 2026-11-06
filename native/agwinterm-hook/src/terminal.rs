use std::{
    fs::File,
    io::{self, Read, Write},
    os::{
        fd::{AsRawFd, FromRawFd},
        unix::process::CommandExt,
    },
    process::Command,
};

fn size(cols: u16, rows: u16) -> libc::winsize {
    libc::winsize {
        ws_row: rows,
        ws_col: cols,
        ws_xpixel: 0,
        ws_ypixel: 0,
    }
}

// stdin frames: a big-endian u32 byte count followed by input. A zero count
// carries two big-endian u16 values (columns, rows). stdout is verbatim VT.
fn input(mut master: File) -> io::Result<()> {
    let mut stdin = io::stdin().lock();
    let mut data = [0; 8192];
    loop {
        let mut header = [0; 4];
        stdin.read_exact(&mut header)?;
        let mut remaining = u32::from_be_bytes(header) as usize;
        if remaining == 0 {
            stdin.read_exact(&mut header)?;
            let [a, b, c, d] = header;
            let win = size(u16::from_be_bytes([a, b]), u16::from_be_bytes([c, d]));
            if unsafe { libc::ioctl(master.as_raw_fd(), libc::TIOCSWINSZ, &win) } < 0 {
                return Err(io::Error::last_os_error());
            }
        }
        while remaining > 0 {
            let n = remaining.min(data.len());
            stdin.read_exact(&mut data[..n])?;
            master.write_all(&data[..n])?;
            remaining -= n;
        }
    }
}

pub fn run() -> Result<i32, Box<dyn std::error::Error>> {
    let mut args = std::env::args_os().skip(2);
    let mut dimension = || {
        args.next()
            .ok_or("missing dimensions")?
            .into_string()
            .map_err(|_| "invalid dimensions")?
            .parse::<u16>()
            .map_err(|_| "invalid dimensions")
    };
    let win = size(dimension()?, dimension()?);
    let mut command = Command::new(args.next().ok_or("missing command")?);
    command.args(args);
    if std::env::var_os("TERM").is_none() {
        command.env("TERM", "xterm-256color");
    }

    let mut fd = -1;
    // This subcommand runs before the watchdog or any other thread starts.
    let pid = unsafe { libc::forkpty(&mut fd, std::ptr::null_mut(), std::ptr::null(), &win) };
    if pid < 0 {
        return Err(io::Error::last_os_error().into());
    }
    if pid == 0 {
        eprintln!("agwinterm: {}", command.exec());
        unsafe { libc::_exit(127) }
    }
    let mut master = unsafe { File::from_raw_fd(fd) };
    // A background job can still hold the slave after the command exits.
    // Observe the actual child alongside the master, then drain queued output.
    let (child, wake) = std::os::unix::net::UnixStream::pair()?;
    let waiter = std::thread::spawn(move || {
        let mut status = 0;
        loop {
            if unsafe { libc::waitpid(pid, &mut status, 0) } == pid {
                break;
            }
            let error = io::Error::last_os_error();
            if error.kind() != io::ErrorKind::Interrupted {
                return Err(error);
            }
        }
        drop(wake); // wake poll even if a grandchild still has the slave open
        Ok(status)
    });
    let writer = master.try_clone()?;
    std::thread::spawn(move || {
        // Loss of the Windows endpoint closes the PTY master and hangs up the
        // foreground client. A detached ZMX daemon owns its own separate PTY.
        let _ = input(writer);
        std::process::exit(1);
    });
    let mut output = io::stdout().lock();
    let mut buffer = [0; 16384];
    let mut descriptors = [
        libc::pollfd {
            fd,
            events: libc::POLLIN,
            revents: 0,
        },
        libc::pollfd {
            fd: child.as_raw_fd(),
            events: libc::POLLIN,
            revents: 0,
        },
    ];
    let mut drained_after_exit = 0;
    loop {
        if unsafe { libc::poll(descriptors.as_mut_ptr(), 2, -1) } < 0 {
            let error = io::Error::last_os_error();
            if error.kind() == io::ErrorKind::Interrupted {
                continue;
            }
            return Err(error.into());
        }
        if descriptors[0].revents == 0 {
            break;
        }
        match master.read(&mut buffer) {
            Ok(0) => break,
            Ok(n) => {
                output.write_all(&buffer[..n])?;
                output.flush()?;
                // A descendant can keep producing output after the session's
                // child has exited. Bound that final drain.
                if descriptors[1].revents != 0 {
                    drained_after_exit += n;
                    if drained_after_exit >= 1024 * 1024 {
                        break;
                    }
                }
            }
            Err(e) if e.raw_os_error() == Some(libc::EIO) => break,
            Err(e) if e.kind() == io::ErrorKind::Interrupted => continue,
            Err(e) => return Err(e.into()),
        }
    }
    let status = waiter.join().map_err(|_| "child wait failed")??;
    Ok(if libc::WIFEXITED(status) {
        libc::WEXITSTATUS(status)
    } else {
        128 + libc::WTERMSIG(status)
    })
}
