"""Current user profile endpoint."""

from __future__ import annotations

from flask import Blueprint, g, jsonify
from flask.typing import ResponseReturnValue
from flask_login import current_user, login_required

from app.schemas.user import UserPublic

me_bp = Blueprint("me", __name__)


@me_bp.get("/me")
@login_required
def me() -> ResponseReturnValue:
    result = UserPublic.model_validate(current_user).model_dump(mode="json")
    if "labelsystem_login_name" in g:
        result["email"] = g.labelsystem_login_name
        result["authentication_source"] = "labelsystem"
        result["print_dispatch_enabled"] = False
        result["print_permissions"] = sorted(g.get("labelsystem_codes", set()))
    return jsonify(result)
