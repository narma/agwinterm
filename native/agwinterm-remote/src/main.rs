#[cfg(unix)]
mod bridge;

fn main() {
    #[cfg(unix)]
    bridge::main();
    #[cfg(not(unix))]
    {
        eprintln!("The origin bridge requires macOS or Linux");
        std::process::exit(1);
    }
}
