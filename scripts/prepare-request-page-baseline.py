"""Prepare the historical Details action for an opt-in, same-fixture comparison.

Run from the repository root. Generated source remains in tmp/ and is compiled only
with -p:RequestPageBaseline=true. No application or database configuration is changed.
"""
from pathlib import Path
import subprocess
import sys

revision = sys.argv[1] if len(sys.argv) > 1 else "2b3f22f"
source = subprocess.run(
    ["git", "show", f"{revision}:MCAROC.Portal/MCAROC_Analysis/Controllers/RequestsController.cs"],
    check=True, capture_output=True, encoding="utf-8",
).stdout
start = source.index('    [HttpGet("/Requests/{id:long}")]')
end = source.index('    /// <summary>The computed-metrics', start)
header = source[:source.index('    [HttpGet("/Requests")]')]
header = header.replace("namespace MCAROC_Analysis.Controllers;",
                        "using MCAROC_Analysis.Controllers;\nnamespace MCAROC_Analysis.Tests;")
header = header.replace("public class RequestsController(", "public class BaselineRequestPageController(")
imports = "using Microsoft.AspNetCore.Hosting;\nusing Microsoft.Extensions.Logging;\nusing Microsoft.Extensions.DependencyInjection;\n"
target = Path("tmp/request-page-baseline/RequestsController.cs")
target.parent.mkdir(parents=True, exist_ok=True)
target.write_text(imports + header + source[start:end] + "}\n", encoding="utf-8")
print(f"Prepared baseline from {revision}: {target}")
