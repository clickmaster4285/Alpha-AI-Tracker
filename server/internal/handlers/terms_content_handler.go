package handlers

import (
	"net/http"
	"strings"

	"github.com/alpha-ai-tracker/server/internal/repository"
	"github.com/labstack/echo/v4"
)

type TermsContentHandler struct {
	repo *repository.TermsContentRepo
}

func NewTermsContentHandler(repo *repository.TermsContentRepo) *TermsContentHandler {
	return &TermsContentHandler{repo: repo}
}

type TermsContentResponse struct {
	ID           string `json:"id"`
	Slug         string `json:"slug"`
	Heading      string `json:"heading"`
	Body         string `json:"body"`
	TermsVersion string `json:"termsVersion"`
	IsSystem     bool   `json:"isSystem"`
	SortOrder    int    `json:"sortOrder"`
	UpdatedAt    string `json:"updatedAt"`
	CreatedAt    string `json:"createdAt"`
}

type TermsContentListResponse struct {
	Items []TermsContentResponse `json:"items"`
}

type UpdateTermsContentRequest struct {
	Heading      string `json:"heading"`
	Body         string `json:"body"`
	TermsVersion string `json:"termsVersion"`
}

type CreateTermsContentRequest struct {
	Heading      string `json:"heading"`
	Body         string `json:"body"`
	TermsVersion string `json:"termsVersion"`
}

func toResponse(item *repository.TermsContent) TermsContentResponse {
	return TermsContentResponse{
		ID:           item.ID,
		Slug:         item.Slug,
		Heading:      item.Heading,
		Body:         item.Body,
		TermsVersion: item.TermsVersion,
		IsSystem:     item.IsSystem,
		SortOrder:    item.SortOrder,
		UpdatedAt:    item.UpdatedAt.Format("2006-01-02T15:04:05Z"),
		CreatedAt:    item.CreatedAt.Format("2006-01-02T15:04:05Z"),
	}
}

func (h *TermsContentHandler) ListTermsContent(c echo.Context) error {
	ctx := c.Request().Context()
	items, err := h.repo.ListAll(ctx)
	if err != nil {
		return c.JSON(http.StatusInternalServerError, map[string]string{"error": err.Error()})
	}

	resp := make([]TermsContentResponse, len(items))
	for i, item := range items {
		resp[i] = toResponse(&item)
	}

	return c.JSON(http.StatusOK, TermsContentListResponse{Items: resp})
}

func (h *TermsContentHandler) GetTermsContent(c echo.Context) error {
	id := c.Param("id")
	ctx := c.Request().Context()

	item, err := h.repo.GetByID(ctx, id)
	if err != nil {
		return c.JSON(http.StatusNotFound, map[string]string{"error": "terms content not found"})
	}

	return c.JSON(http.StatusOK, toResponse(item))
}

func (h *TermsContentHandler) UpdateTermsContent(c echo.Context) error {
	id := c.Param("id")
	var req UpdateTermsContentRequest
	if err := c.Bind(&req); err != nil {
		return c.JSON(http.StatusBadRequest, map[string]string{"error": "invalid request body"})
	}

	if strings.TrimSpace(req.Heading) == "" {
		return c.JSON(http.StatusBadRequest, map[string]string{"error": "heading is required"})
	}

	ctx := c.Request().Context()
	item, err := h.repo.Update(ctx, id, req.Heading, req.Body, req.TermsVersion)
	if err != nil {
		return c.JSON(http.StatusInternalServerError, map[string]string{"error": err.Error()})
	}

	return c.JSON(http.StatusOK, toResponse(item))
}

func (h *TermsContentHandler) CreateTermsContent(c echo.Context) error {
	var req CreateTermsContentRequest
	if err := c.Bind(&req); err != nil {
		return c.JSON(http.StatusBadRequest, map[string]string{"error": "invalid request body"})
	}

	if strings.TrimSpace(req.Heading) == "" {
		return c.JSON(http.StatusBadRequest, map[string]string{"error": "heading is required"})
	}

	// Auto-generate slug from heading
	slug := strings.ToLower(strings.TrimSpace(req.Heading))
	slug = strings.ReplaceAll(slug, " ", "_")
	slug = strings.ReplaceAll(slug, "-", "_")
	// Remove non-alphanumeric/underscore chars
	var cleaned []rune
	for _, r := range slug {
		if (r >= 'a' && r <= 'z') || (r >= '0' && r <= '9') || r == '_' {
			cleaned = append(cleaned, r)
		}
	}
	slug = string(cleaned)

	if req.TermsVersion == "" {
		req.TermsVersion = "1.0"
	}

	ctx := c.Request().Context()
	maxOrder := h.repo.GetMaxSortOrder(ctx)

	item, err := h.repo.Create(ctx, slug, req.Heading, req.Body, req.TermsVersion, maxOrder+1)
	if err != nil {
		return c.JSON(http.StatusInternalServerError, map[string]string{"error": err.Error()})
	}

	return c.JSON(http.StatusCreated, toResponse(item))
}

func (h *TermsContentHandler) DeleteTermsContent(c echo.Context) error {
	id := c.Param("id")
	ctx := c.Request().Context()

	// Check if it's a system term — cannot delete
	item, err := h.repo.GetByID(ctx, id)
	if err != nil {
		return c.JSON(http.StatusNotFound, map[string]string{"error": "terms content not found"})
	}
	if item.IsSystem {
		return c.JSON(http.StatusForbidden, map[string]string{"error": "system terms cannot be deleted"})
	}

	if err := h.repo.Delete(ctx, id); err != nil {
		return c.JSON(http.StatusInternalServerError, map[string]string{"error": err.Error()})
	}

	return c.JSON(http.StatusOK, map[string]string{"message": "deleted"})
}
