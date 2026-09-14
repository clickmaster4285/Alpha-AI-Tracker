package handlers

import (
	"net/http"

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
	FeatureID    string `json:"featureId"`
	Heading      string `json:"heading"`
	Body         string `json:"body"`
	TermsVersion string `json:"termsVersion"`
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

func (h *TermsContentHandler) ListTermsContent(c echo.Context) error {
	ctx := c.Request().Context()
	items, err := h.repo.ListAll(ctx)
	if err != nil {
		return c.JSON(http.StatusInternalServerError, map[string]string{"error": err.Error()})
	}

	resp := make([]TermsContentResponse, len(items))
	for i, item := range items {
		resp[i] = TermsContentResponse{
			ID:           item.ID,
			FeatureID:    item.FeatureID,
			Heading:      item.Heading,
			Body:         item.Body,
			TermsVersion: item.TermsVersion,
			UpdatedAt:    item.UpdatedAt.Format("2006-01-02T15:04:05Z"),
			CreatedAt:    item.CreatedAt.Format("2006-01-02T15:04:05Z"),
		}
	}

	return c.JSON(http.StatusOK, TermsContentListResponse{Items: resp})
}

func (h *TermsContentHandler) GetTermsContent(c echo.Context) error {
	featureID := c.Param("featureId")
	ctx := c.Request().Context()

	item, err := h.repo.GetByFeatureID(ctx, featureID)
	if err != nil {
		return c.JSON(http.StatusNotFound, map[string]string{"error": "feature not found"})
	}

	return c.JSON(http.StatusOK, TermsContentResponse{
		ID:           item.ID,
		FeatureID:    item.FeatureID,
		Heading:      item.Heading,
		Body:         item.Body,
		TermsVersion: item.TermsVersion,
		UpdatedAt:    item.UpdatedAt.Format("2006-01-02T15:04:05Z"),
		CreatedAt:    item.CreatedAt.Format("2006-01-02T15:04:05Z"),
	})
}

func (h *TermsContentHandler) UpdateTermsContent(c echo.Context) error {
	featureID := c.Param("featureId")
	var req UpdateTermsContentRequest
	if err := c.Bind(&req); err != nil {
		return c.JSON(http.StatusBadRequest, map[string]string{"error": "invalid request body"})
	}

	if req.Heading == "" {
		return c.JSON(http.StatusBadRequest, map[string]string{"error": "heading is required"})
	}

	ctx := c.Request().Context()
	item, err := h.repo.Upsert(ctx, featureID, req.Heading, req.Body, req.TermsVersion)
	if err != nil {
		return c.JSON(http.StatusInternalServerError, map[string]string{"error": err.Error()})
	}

	return c.JSON(http.StatusOK, TermsContentResponse{
		ID:           item.ID,
		FeatureID:    item.FeatureID,
		Heading:      item.Heading,
		Body:         item.Body,
		TermsVersion: item.TermsVersion,
		UpdatedAt:    item.UpdatedAt.Format("2006-01-02T15:04:05Z"),
		CreatedAt:    item.CreatedAt.Format("2006-01-02T15:04:05Z"),
	})
}
