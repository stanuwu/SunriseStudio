use std::{env, fs, io::{self, Read}, path::PathBuf, process::ExitCode};

use parhelion::{BatchBuildRequest, BatchBuildSnapshot, WeaponRecipe, build_and_stage_snapshot_with_progress};
use serde_json::json;

fn fail(message: impl std::fmt::Display) -> ExitCode {
    eprintln!("{message}");
    ExitCode::FAILURE
}

fn main() -> ExitCode {
    let mut args = env::args().skip(1);
    match args.next().as_deref() {
        Some("validate-json") => validate_stdin(),
        Some("build") => build(args.collect()),
        _ => fail("Usage: solforge-parhelion-bridge validate-json | build <package-directory> <staging-root> <recipe>...")
    }
}

fn validate_stdin() -> ExitCode {
    let mut encoded = String::new();
    if let Err(error) = io::stdin().read_to_string(&mut encoded) { return fail(error); }
    match WeaponRecipe::from_json_str(&encoded) {
        Ok(recipe) => match recipe.to_json_pretty() {
            Ok(canonical) => { println!("{canonical}"); ExitCode::SUCCESS }
            Err(error) => fail(error),
        },
        Err(error) => fail(error),
    }
}

fn build(args: Vec<String>) -> ExitCode {
    if args.len() < 3 { return fail("Build needs a source package directory, a staging root, and at least one recipe path."); }
    let package_directory = PathBuf::from(&args[0]);
    let staging_root = PathBuf::from(&args[1]);
    let mut recipes = Vec::with_capacity(args.len() - 2);
    for path in &args[2..] {
        let encoded = match fs::read_to_string(path) { Ok(value) => value, Err(error) => return fail(format!("Could not read recipe {path}: {error}")) };
        match WeaponRecipe::from_json_str(&encoded) { Ok(recipe) => recipes.push(recipe), Err(error) => return fail(format!("Invalid recipe {path}: {error}")) }
    }
    let request = BatchBuildRequest { package_directory, staging_root, ignore_installed_authored_overlays: false, recipes, maps: vec![] };
    let snapshot = match BatchBuildSnapshot::new(request) { Ok(value) => value, Err(error) => return fail(error) };
    let report = match build_and_stage_snapshot_with_progress(&snapshot, |progress| eprintln!("{}: {}", progress.phase.label(), progress.current_artifact.as_deref().unwrap_or(""))) {
        Ok(value) => value, Err(error) => return fail(error),
    };
    let artifacts: Vec<_> = report.artifacts.iter().map(|artifact| json!({
        "file_name": artifact.file_name,
        "byte_length": artifact.byte_length,
        "sha256": artifact.sha256,
    })).collect();
    println!("{}", json!({
        "run_directory": report.run_directory,
        "manifest_path": report.manifest_path,
        "selection_fingerprint": report.selection_fingerprint,
        "artifacts": artifacts,
        "staged_recipe_paths": report.staged_recipe_paths,
    }));
    ExitCode::SUCCESS
}
