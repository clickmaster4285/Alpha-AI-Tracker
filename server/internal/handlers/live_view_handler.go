package handlers

import (
	"errors"
	"net/http"
	"strconv"

	"github.com/alpha-ai-tracker/server/internal/dto"
	"github.com/alpha-ai-tracker/server/internal/services"
	"github.com/labstack/echo/v4"
)

type LiveViewHandler struct {
	service *services.LiveViewService
}

func NewLiveViewHandler(service *services.LiveViewService) *LiveViewHandler {
	return &LiveViewHandler{service: service}
}

func liveViewUserID(c echo.Context) (string, error) {
	userID, ok := c.Get("user_id").(string)
	if !ok || userID == "" {
		return "", errors.New("authenticated user not found")
	}
	return userID, nil
}

func (h *LiveViewHandler) Create(c echo.Context) error {
	userID, err := liveViewUserID(c)
	if err != nil {
		return c.JSON(http.StatusUnauthorized, dto.APIError{Code: http.StatusUnauthorized, Message: err.Error()})
	}
	var req dto.CreateLiveViewSessionRequest
	if err := c.Bind(&req); err != nil {
		return c.JSON(http.StatusBadRequest, dto.APIError{Code: http.StatusBadRequest, Message: "Invalid request body"})
	}
	session, err := h.service.Create(c.Request().Context(), userID, &req, c.Response().Header().Get(echo.HeaderXRequestID))
	if err != nil {
		return liveViewError(c, err)
	}
	return c.JSON(http.StatusCreated, session)
}

func (h *LiveViewHandler) List(c echo.Context) error {
	userID, err := liveViewUserID(c)
	if err != nil {
		return c.JSON(http.StatusUnauthorized, dto.APIError{Code: http.StatusUnauthorized, Message: err.Error()})
	}
	page, _ := strconv.Atoi(c.QueryParam("page"))
	perPage, _ := strconv.Atoi(c.QueryParam("perPage"))
	result, err := h.service.List(c.Request().Context(), userID, page, perPage)
	if err != nil {
		return liveViewError(c, err)
	}
	return c.JSON(http.StatusOK, result)
}

func (h *LiveViewHandler) Get(c echo.Context) error {
	userID, err := liveViewUserID(c)
	if err != nil {
		return c.JSON(http.StatusUnauthorized, dto.APIError{Code: http.StatusUnauthorized, Message: err.Error()})
	}
	session, err := h.service.Get(c.Request().Context(), userID, c.Param("id"))
	if err != nil {
		return liveViewError(c, err)
	}
	return c.JSON(http.StatusOK, session)
}

func (h *LiveViewHandler) Stop(c echo.Context) error {
	userID, err := liveViewUserID(c)
	if err != nil {
		return c.JSON(http.StatusUnauthorized, dto.APIError{Code: http.StatusUnauthorized, Message: err.Error()})
	}
	var body struct {
		Reason string `json:"reason"`
	}
	if err := c.Bind(&body); err != nil {
		return c.JSON(http.StatusBadRequest, dto.APIError{Code: http.StatusBadRequest, Message: "Invalid request body"})
	}
	session, err := h.service.Stop(c.Request().Context(), userID, c.Param("id"), body.Reason, c.Response().Header().Get(echo.HeaderXRequestID))
	if err != nil {
		return liveViewError(c, err)
	}
	return c.JSON(http.StatusOK, session)
}

func liveViewError(c echo.Context, err error) error {
	code := http.StatusInternalServerError
	switch {
	case errors.Is(err, services.ErrLiveViewForbidden):
		code = http.StatusForbidden
	case errors.Is(err, services.ErrLiveViewNotFound):
		code = http.StatusNotFound
	case errors.Is(err, services.ErrLiveViewInactive):
		code = http.StatusConflict
	case errors.Is(err, services.ErrLiveViewInvalid):
		code = http.StatusBadRequest
	}
	return c.JSON(code, dto.APIError{Code: code, Message: err.Error()})
}
