"""Enforce the minimum new-code severity gate independently of project gate settings."""
import json
import os
import urllib.parse
import urllib.request


def check(fetch, project, pull_request=""):
    query = {"componentKeys": project, "severities": "BLOCKER,CRITICAL",
             "resolved": "false", "sinceLeakPeriod": "true", "ps": "1"}
    if pull_request:
        query["pullRequest"] = pull_request
    result = fetch(query)
    total = result.get("paging", {}).get("total", result.get("total"))
    if not isinstance(total, int) or total < 0:
        raise RuntimeError("SonarCloud returned no valid issue count.")
    if total:
        raise RuntimeError(f"Quality gate failed: {total} new blocker/critical issue(s).")
    print("No new blocker or critical issues.")


def fetch(query):
    url = "https://sonarcloud.io/api/issues/search?" + urllib.parse.urlencode(query)
    request = urllib.request.Request(url, headers={"Authorization": "Bearer " + os.environ["SONAR_TOKEN"]})
    with urllib.request.urlopen(request, timeout=60) as response:
        return json.load(response)


if __name__ == "__main__":
    check(fetch, os.environ["SONAR_PROJECT_KEY"], os.getenv("SONAR_PULL_REQUEST", ""))
