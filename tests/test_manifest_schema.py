"""schemas/integration.schema.json is the published structural contract.

Two failure modes matter, and there is a test for each:

  too strict     — the schema rejects a manifest that actually works. Caught by
                   validating every manifest committed to this repo.
  too permissive — the schema accepts something the orchestrator will reject, or
                   worse, something dangerous like an inline credential. Caught by
                   the rejection cases below.

The orchestrator keeps its own imperative validation rather than consuming this
schema at runtime, on purpose: the MCP agent reads validation errors to correct
its own output, and "credentialRef must be a name, not the credential itself"
teaches it something that "does not match schema" does not. The cost is that the
two can drift, which is what these tests are for.
"""

import json
from pathlib import Path

import pytest
import yaml
from jsonschema import Draft202012Validator

REPO = Path(__file__).resolve().parents[1]
SCHEMA = json.loads((REPO / "schemas" / "integration.schema.json").read_text())
VALIDATOR = Draft202012Validator(SCHEMA)

MANIFESTS = sorted((REPO / "integrations").glob("*.yaml"))


def errors(doc: dict) -> list[str]:
    return [e.message for e in VALIDATOR.iter_errors(doc)]


def test_the_schema_is_itself_a_valid_json_schema():
    Draft202012Validator.check_schema(SCHEMA)


def test_there_are_manifests_to_check():
    # Guards against this whole file silently passing on an empty glob.
    assert MANIFESTS, "no manifests found in integrations/"


@pytest.mark.parametrize("path", MANIFESTS, ids=lambda p: p.name)
def test_every_shipped_manifest_validates(path: Path):
    assert errors(yaml.safe_load(path.read_text())) == []


def base(**spec_overrides) -> dict:
    spec = {
        "protocol": "rest",
        "baseUrl": "https://api.example.test",
        "auth": {"type": "none"},
        "resources": [
            {"name": "thing", "method": "GET", "path": "/thing",
             "emit": "single", "transform": "{ id: to_string(id) }"}
        ],
    }
    spec.update(spec_overrides)
    return {
        "apiVersion": "integrationhub.dev/v1alpha1",
        "kind": "Integration",
        "metadata": {"id": "demo", "displayName": "Demo"},
        "spec": spec,
    }


def test_the_baseline_fixture_is_valid():
    # Otherwise every rejection test below could pass for the wrong reason.
    assert errors(base()) == []


class TestRejections:
    def test_unknown_api_version(self):
        doc = base()
        doc["apiVersion"] = "integrationhub.dev/v1"
        assert errors(doc)

    @pytest.mark.parametrize("bad_id", ["UPPER", "under_score", "has space", "", "x" * 41])
    def test_malformed_id(self, bad_id):
        doc = base()
        doc["metadata"]["id"] = bad_id
        assert errors(doc)

    @pytest.mark.parametrize(
        "field", ["token", "value", "secret", "password", "apiKey"]
    )
    def test_an_inline_secret_in_the_auth_block(self, field):
        """The dangerous one. additionalProperties:false is what stops it."""
        doc = base(auth={"type": "bearer", "credentialRef": "github-token",
                         field: "hunter2-actual-secret"})
        assert errors(doc)

    def test_a_credentialRef_that_is_actually_a_token(self):
        doc = base(auth={"type": "bearer",
                         "credentialRef": "ghp_R2d2C3poNotARealTokenButShapedLikeOne"})
        assert errors(doc)

    def test_bearer_without_a_credentialRef(self):
        assert errors(base(auth={"type": "bearer"}))

    def test_auth_none_carrying_a_credentialRef(self):
        assert errors(base(auth={"type": "none", "credentialRef": "github-token"}))

    def test_headerKey_without_a_headerName(self):
        assert errors(base(auth={"type": "headerKey", "credentialRef": "some-key"}))

    def test_relative_base_url(self):
        assert errors(base(baseUrl="/api"))

    def test_no_resources(self):
        assert errors(base(resources=[]))

    def test_resource_without_a_transform(self):
        assert errors(base(resources=[{"name": "thing", "path": "/thing"}]))

    def test_empty_transform(self):
        assert errors(base(resources=[
            {"name": "thing", "path": "/thing", "transform": ""}]))

    def test_unknown_emit(self):
        assert errors(base(resources=[
            {"name": "thing", "path": "/t", "emit": "stream", "transform": "@"}]))

    def test_the_reserved_handler_escape_hatch(self):
        assert errors(base(resources=[
            {"name": "thing", "path": "/t", "transform": "@",
             "handler": "mypkg.mod:fn"}]))

    def test_a_typo_in_a_known_field(self):
        assert errors(base(resources=[
            {"name": "thing", "methd": "GET", "path": "/t", "transform": "@"}]))

    def test_param_both_required_and_defaulted(self):
        assert errors(base(resources=[
            {"name": "thing", "path": "/t", "transform": "@",
             "params": [{"name": "x", "in": "query", "required": True, "default": "1"}]}]))

    def test_unknown_param_location(self):
        assert errors(base(resources=[
            {"name": "thing", "path": "/t", "transform": "@",
             "params": [{"name": "x", "in": "cookie"}]}]))

    def test_resource_name_that_would_not_survive_a_url(self):
        assert errors(base(resources=[
            {"name": "not a name", "path": "/t", "transform": "@"}]))


class TestAcceptances:
    """Things that must NOT be rejected — the schema being too strict is a real bug."""

    def test_bearer_auth_with_a_proper_reference(self):
        assert errors(base(auth={"type": "bearer", "credentialRef": "github-token"})) == []

    def test_header_key_auth(self):
        assert errors(base(auth={"type": "headerKey", "credentialRef": "some-key",
                                 "headerName": "X-Api-Key"})) == []

    def test_full_resiliency_block(self):
        assert errors(base(resiliency={
            "retry": {"maxAttempts": 3, "backoff": "exponential",
                      "baseDelayMs": 200, "jitter": True},
            "circuitBreaker": {"failureRatio": 0.5, "samplingSeconds": 30,
                               "breakSeconds": 15, "minThroughput": 8},
        })) == []

    def test_graphql_protocol_with_a_query_and_variables(self):
        assert errors(base(protocol="graphql", resources=[{
            "name": "repoTopics",
            "query": "query($owner:String!){ repository(owner:$owner){ id } }",
            "params": [{"name": "owner", "in": "variable", "required": True}],
            "emit": "list",
            "transform": "data.repository[].{ id: id }",
        }])) == []

    def test_defaults_block_with_headers(self):
        assert errors(base(defaults={
            "headers": {"Accept": "application/json"}, "timeoutMs": 3000})) == []
